using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Momus.Core.Ingest;

namespace Momus.Client.Internal;

/// <summary>
/// Opens an operation around every request. It is inserted first in the pipeline, so a query run
/// by any middleware is still attributed — the operation's name is worked out lazily, by which
/// time routing has run and the endpoint is known.
/// </summary>
internal sealed class MomusMiddleware(RequestDelegate next, OperationQueue queue)
{
    public async Task InvokeAsync(HttpContext context)
    {
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
