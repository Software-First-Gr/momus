using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Momus.Core.Ingest;
using Momus.Server;

namespace Momus.Tests;

/// <summary>
/// Who may report, and what the ingest port lets through. The defaults assume a server on the same
/// machine as the application; these are the two settings for the day it is not — a key, and a
/// second port that serves the ingest endpoint and nothing else, so the pages can stay on loopback.
/// Each test runs the real server on real loopback ports.
/// </summary>
public class IngestSecurityTests
{
    [Fact]
    public async Task Without_a_key_a_window_is_accepted_as_it_always_was()
    {
        await using var server = await TestServer.StartAsync();

        var response = await server.PostWindowAsync(server.MainPort, key: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task With_a_key_a_window_needs_that_key()
    {
        await using var server = await TestServer.StartAsync(key: "s3cret");

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.PostWindowAsync(server.MainPort, key: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.PostWindowAsync(server.MainPort, key: "s3cret2")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await server.PostWindowAsync(server.MainPort, key: "s3cret")).StatusCode);
    }

    [Fact]
    public async Task A_refused_window_is_a_problem_on_the_diagnostics_page()
    {
        // An application whose key is wrong looks like one that is not running on every other line.
        await using var server = await TestServer.StartAsync(key: "s3cret");
        await server.PostWindowAsync(server.MainPort, key: "wrong");

        var diagnostics = await server.GetStringAsync(server.MainPort, "/api/v1/diagnostics");

        Assert.Contains("1 window(s) refused", diagnostics);
        Assert.Contains("MOMUS_INGEST_KEY", diagnostics);
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/settings")]
    [InlineData("POST", "/settings")]
    [InlineData("GET", "/queries")]
    [InlineData("GET", "/api/v1/diagnostics")]
    [InlineData("GET", "/api/v1/ingest")]
    public async Task The_ingest_port_serves_nothing_but_ingest_and_the_health_check(string method, string path)
    {
        await using var server = await TestServer.StartAsync(key: "s3cret", ingestPort: true);

        var response = await server.SendAsync(server.IngestPort!.Value, new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task On_the_ingest_port_ingest_and_the_health_check_work_and_the_pages_stay_on_the_main_port()
    {
        await using var server = await TestServer.StartAsync(key: "s3cret", ingestPort: true);

        Assert.Equal(HttpStatusCode.Accepted, (await server.PostWindowAsync(server.IngestPort!.Value, key: "s3cret")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(server.IngestPort!.Value, new HttpRequestMessage(HttpMethod.Get, "/healthz"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(server.MainPort, new HttpRequestMessage(HttpMethod.Get, "/"))).StatusCode);
    }

    [Fact]
    public async Task A_host_header_naming_the_main_port_does_not_open_the_pages_on_the_ingest_port()
    {
        // The port a connection arrived on decides, not the Host header, which the caller writes.
        await using var server = await TestServer.StartAsync(key: "s3cret", ingestPort: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
        request.Headers.Host = $"127.0.0.1:{server.MainPort}";

        Assert.Equal(HttpStatusCode.NotFound, (await server.SendAsync(server.IngestPort!.Value, request)).StatusCode);
    }

    [Fact]
    public async Task An_ingest_port_without_a_key_is_a_warning()
    {
        await using var server = await TestServer.StartAsync(ingestPort: true);

        var diagnostics = await server.GetStringAsync(server.MainPort, "/api/v1/diagnostics");

        Assert.Contains($"Port {server.IngestPort} accepts windows from anything that can reach it", diagnostics);
    }

    [Fact]
    public void The_ingest_port_has_to_be_a_different_port()
    {
        Assert.NotNull(new ServerOptions { Port = 4848, IngestPort = 4848 }.Problem());
        Assert.Null(new ServerOptions { Port = 4848, IngestPort = 4849 }.Problem());
        Assert.Null(new ServerOptions { Port = 4848 }.Problem());
    }

    private sealed class TestServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly DirectoryInfo _data;
        private readonly HttpClient _http = new();

        private TestServer(WebApplication app, ServerOptions options, DirectoryInfo data)
        {
            _app = app;
            _data = data;
            MainPort = options.Port;
            IngestPort = options.IngestPort;
        }

        public int MainPort { get; }
        public int? IngestPort { get; }

        public static async Task<TestServer> StartAsync(string? key = null, bool ingestPort = false)
        {
            var data = Directory.CreateTempSubdirectory("momus-ingest-");
            var main = FreePort();
            var options = new ServerOptions
            {
                DataDirectory = data.FullName,
                ListenAddress = "127.0.0.1",
                Port = main,
                IngestPort = ingestPort ? FreePort(except: main) : null,
                IngestKey = key,
            };

            var app = await MomusServer.BuildAsync(options);
            await app.StartAsync();
            return new TestServer(app, options, data);
        }

        public Task<HttpResponseMessage> PostWindowAsync(int port, string? key)
        {
            var now = DateTimeOffset.UtcNow;
            var batch = new IngestBatch
            {
                App = new IngestApp("Shop.Api", "1.0.0", "test:1", "Test"),
                Window = new IngestWindow(now.AddSeconds(-5), now),
            };
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest")
            {
                Content = JsonContent.Create(batch, options: IngestJson.Options),
            };
            if (key is not null) request.Headers.Add(IngestJson.KeyHeader, key);
            return SendAsync(port, request);
        }

        public Task<HttpResponseMessage> SendAsync(int port, HttpRequestMessage request)
        {
            request.RequestUri = new Uri($"http://127.0.0.1:{port}{request.RequestUri}");
            return _http.SendAsync(request);
        }

        public async Task<string> GetStringAsync(int port, string path)
        {
            var response = await SendAsync(port, new HttpRequestMessage(HttpMethod.Get, path));
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private static int FreePort(int except = 0)
        {
            while (true)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                if (port != except) return port;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                _data.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is not worth failing a test over.
            }
        }
    }
}
