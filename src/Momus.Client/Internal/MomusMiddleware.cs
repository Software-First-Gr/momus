using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Momus.Core.Ingest;

namespace Momus.Client.Internal;

/// <summary>
/// Opens an operation around every request that is a unit of work — all of them except WebSockets
/// and event streams. It is inserted first in the pipeline, so a query run by any middleware is
/// still attributed — the operation's name is worked out lazily, by which time routing has run and
/// the endpoint is known.
/// </summary>
internal sealed class MomusMiddleware(RequestDelegate next, OperationQueue queue)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // A WebSocket or an event stream is a connection, not a unit of work. A Blazor Server
        // circuit is one WebSocket for as long as its tab is open; as one operation, everything
        // the UI ran was reported only when the tab closed, and any lookup repeated across the
        // session read as a loop inside one request. Its statements are ambient instead, named
        // after the innermost Activity — a mediator's Send span or a job's root span when the
        // application has them.
        if (IsLongLived(context))
        {
            await next(context);
            return;
        }

        var operation = OperationContext.Begin(IngestOperation.Http, context, null);
        try
        {
            await next(context);
        }
        finally
        {
            OperationContext.End(operation);

            // Only requests that touched the database are worth a row.
            if (operation.QueryCount > 0) queue.Enqueue(operation.Complete(countsAsOperation: true));
        }
    }

    /// <summary>
    /// Read from the handshake rather than from <c>HttpContext.WebSockets</c>: this middleware runs
    /// before <c>UseWebSockets</c>, and until that has run a WebSocket request does not say it is one.
    /// </summary>
    internal static bool IsLongLived(HttpContext context)
    {
        var request = context.Request;

        // HTTP/1.1 WebSockets upgrade the connection; HTTP/2 ones arrive as an extended CONNECT.
        if (request.Headers.Upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase)) return true;
        if (context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true }) return true;

        foreach (var accept in request.Headers.Accept)
        {
            if (accept?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true) return true;
        }

        return false;
    }
}

/// <summary>
/// Puts the middleware in the pipeline without the application calling <c>UseMomus()</c>. One line
/// in Program.cs was the promise; this is what keeps it to one line.
/// </summary>
internal sealed class MomusStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<MomusMiddleware>();
        next(app);
    };
}
