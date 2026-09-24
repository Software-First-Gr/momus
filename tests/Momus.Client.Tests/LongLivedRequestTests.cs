using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Momus.Client.Internal;
using SampleApp;

namespace Momus.Client.Tests;

/// <summary>
/// An operation is one request, and that is only right while a request is a unit of work. A Blazor
/// Server circuit is a single WebSocket request for as long as the tab stays open, so every
/// statement its UI ran used to land in one operation: reported only when the tab closed, and
/// repeating the same statement far past the five that <c>n_plus_one</c> reads as a loop.
/// </summary>
[Collection(ClientCollection.Name)]
public class LongLivedRequestTests
{
    [Fact]
    public async Task A_websocket_is_reported_while_it_is_open_and_never_reads_as_a_loop()
    {
        await using var app = await WebTestApp.StartAsync(web =>
        {
            web.UseWebSockets();
            web.Map("/ws", async (HttpContext context) =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();

                // One scope for the connection's lifetime, the way a circuit holds its services.
                var db = context.RequestServices.GetRequiredService<WidgetDb>();
                var buffer = new byte[16];
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        return;
                    }

                    // One UI event: the same lookup three times.
                    for (var i = 0; i < 3; i++) await WidgetQueries.LoadOneAsync(db, 1);
                    await socket.SendAsync("done"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            });
        });

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://{app.BaseAddress.Authority}/ws"), CancellationToken.None);
        for (var events = 0; events < 3; events++)
        {
            await client.SendAsync("go"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await client.ReceiveAsync(new byte[16], CancellationToken.None);
        }

        // Still open: a tab left open all day must not mean a day without numbers.
        var whileOpen = app.Drain();
        Assert.Equal(9, whileOpen.SelectMany(o => o.Queries).Sum(q => q.Count));
        Assert.All(whileOpen.SelectMany(o => o.Queries), q => Assert.Equal(1, q.Count));
        Assert.All(whileOpen, o => Assert.False(o.CountsAsOperation));

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await Task.Delay(200);

        // And closing it does not deliver the whole session as one request.
        Assert.DoesNotContain(app.Drain(), o => o.Queries.Any(q => q.Count > 1));
    }

    [Fact]
    public async Task A_server_sent_event_stream_is_reported_while_it_is_open()
    {
        var release = new TaskCompletionSource();
        await using var app = await WebTestApp.StartAsync(web =>
        {
            web.MapGet("/events", async (HttpContext context, WidgetDb db) =>
            {
                context.Response.ContentType = "text/event-stream";
                for (var i = 0; i < 3; i++) await WidgetQueries.LoadOneAsync(db, 1);
                await context.Response.WriteAsync("data: loaded\n\n");
                await context.Response.Body.FlushAsync();
                await release.Task;
            });
        });

        try
        {
            using var http = new HttpClient { BaseAddress = app.BaseAddress };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
            Assert.Equal("data: loaded", await reader.ReadLineAsync());

            var whileOpen = app.Drain();
            Assert.Equal(3, whileOpen.SelectMany(o => o.Queries).Sum(q => q.Count));
            Assert.All(whileOpen.SelectMany(o => o.Queries), q => Assert.Equal(1, q.Count));
        }
        finally
        {
            release.TrySetResult(); // or the host waits out its shutdown timeout for the open stream
        }
    }

    [Fact]
    public async Task A_statement_that_outlives_its_request_is_reported_rather_than_lost()
    {
        // Work started by a request and still running after it — a fire-and-forget task, or a
        // circuit whose connection began in an earlier request — carries that request's operation
        // with it. It used to record into it after it had been reported, where nothing read it again.
        var release = new TaskCompletionSource();
        var finished = new TaskCompletionSource();
        await using var app = await WebTestApp.StartAsync(web =>
        {
            web.MapGet("/later", (IServiceScopeFactory scopes) =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await release.Task;
                        using var scope = scopes.CreateScope();
                        await WidgetQueries.LoadOneAsync(scope.ServiceProvider.GetRequiredService<WidgetDb>(), 1);
                        finished.SetResult();
                    }
                    catch (Exception ex)
                    {
                        // The application's own query failing is the worst thing the client can do.
                        finished.SetException(ex);
                    }
                });
                return Results.Ok();
            });
        });

        using var http = new HttpClient { BaseAddress = app.BaseAddress };
        (await http.GetAsync("/later")).EnsureSuccessStatusCode();
        await Task.Delay(100);

        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var operation = Assert.Single(app.Drain());
        Assert.Equal(1, Assert.Single(operation.Queries).Count);
        Assert.False(operation.CountsAsOperation);
    }
}
