using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Momus.Core.Ingest;

namespace Momus.Client.Tests;

/// <summary>
/// The client's half of the ingest key: it is sent, and a server refusing it is reported as a
/// refused key — not as a server that is down, which would send someone to check the wrong thing.
/// </summary>
[Collection(ClientCollection.Name)]
public class IngestKeyTests
{
    [Fact]
    public async Task The_key_goes_with_every_window()
    {
        await using var server = await FakeServer.StartAsync(StatusCodes.Status202Accepted);
        await using var app = await WebTestApp.StartAsync(
            configure: o => { o.Endpoint = server.Url; o.IngestKey = "s3cret"; o.FlushSeconds = 1; },
            export: true);

        Assert.Equal("s3cret", await server.FirstKey.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task A_refused_key_is_logged_as_a_refused_key()
    {
        await using var server = await FakeServer.StartAsync(StatusCodes.Status401Unauthorized);
        var logs = new CapturingLogs();
        await using var app = await WebTestApp.StartAsync(
            configure: o => { o.Endpoint = server.Url; o.IngestKey = "wrong"; o.FlushSeconds = 1; },
            export: true,
            logs: logs);

        await server.FirstKey.WaitAsync(TimeSpan.FromSeconds(15));
        var said = await logs.WaitForAsync(m => m.Contains("refused"), TimeSpan.FromSeconds(15));

        Assert.Contains("Momus:IngestKey", said);
        Assert.DoesNotContain(logs.Messages, m => m.Contains("not answering"));
    }

    /// <summary>A stand-in for the Momus server's ingest endpoint that answers with one status.</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource<string> _firstKey = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private FakeServer(WebApplication app) => _app = app;

        public string Url => _app.Urls.First();
        public Task<string> FirstKey => _firstKey.Task;

        public static async Task<FakeServer> StartAsync(int status)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var server = new FakeServer(app);
            app.MapPost("/api/v1/ingest", (HttpRequest request) =>
            {
                server._firstKey.TrySetResult(request.Headers[IngestJson.KeyHeader].ToString());
                return Results.StatusCode(status);
            });
            await app.StartAsync();
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public async Task<string> WaitForAsync(Func<string, bool> match, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (_messages.FirstOrDefault(match) is { } found) return found;
                await Task.Delay(100);
            }
            throw new TimeoutException("The client never said it. It said: " + string.Join(" | ", _messages));
        }

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }
}
