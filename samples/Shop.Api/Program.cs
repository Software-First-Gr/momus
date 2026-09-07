using Microsoft.EntityFrameworkCore;
using Momus.Client;
using Shop.Api;

var builder = WebApplication.CreateBuilder(args);

// One setting to run this app: where the database is. Compose sets it; locally it defaults to a
// Postgres on localhost, which is the same string the Momus quick start uses.
var connectionString = builder.Configuration.GetConnectionString("Shop")
                       ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=shop;Maximum Pool Size=20";
builder.Configuration["ConnectionStrings:Shop"] = connectionString;

// The traffic generator calls this app over HTTP, so request logging is two lines per
// request of noise. What is worth reading is the scenario log in the UI.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);

// Every statement EF sends, printed. Genuinely interesting for about a minute — it is how you
// watch the N+1 happen — and unreadable after that, so it is off unless you ask:
//   docker compose run -e ShowSql=true shop
if (builder.Configuration["ShowSql"] != "true")
{
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
}

builder.Services.AddDbContext<ShopDb>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpClient("self");
builder.Services.AddSingleton<Telemetry>();
builder.Services.AddSingleton<BackgroundJobs>();
builder.Services.AddSingleton<SelfClient>();
builder.Services.AddSingleton(sp => new LoadGenerator(
    sp.GetRequiredService<SelfClient>(),
    sp.GetRequiredService<Telemetry>(),
    sp.GetRequiredService<IHostApplicationLifetime>(),
    sp.GetRequiredService<ILogger<LoadGenerator>>(),
    builder.Configuration["Traffic"] ?? "off"));
builder.Services.AddHostedService(sp => sp.GetRequiredService<LoadGenerator>());
builder.Services.AddScoped<Workload>();

// The other half. One line, after AddDbContext — that ordering is not a style preference: the
// client reaches every context by rewriting the DbContextOptions registrations, so it can only
// see the ones already registered. It will say so at startup if it finds none.
builder.AddMomus();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

await StartupAsync(app);

// ---- the shop's own endpoints -------------------------------------------------------
// Ordinary routes doing ordinary things, one of them badly. These are what the traffic
// generator calls and what M2 will name as operations.

app.MapGet("/api/orders/{id:int}", async (int id, ShopDb db, Workload workload, HttpResponse response, CancellationToken ct) =>
{
    var queries = await workload.OrderPageAsync(db, id, ct);
    response.Headers[SelfClient.QueriesHeader] = queries.ToString();
    return Results.Ok(new { order = id, queries });
});

app.MapGet("/api/search", async (string? q, ShopDb db, Workload workload, HttpResponse response, CancellationToken ct) =>
{
    var results = await workload.SearchAsync(db, q, ct);
    response.Headers[SelfClient.QueriesHeader] = "1";
    return Results.Ok(new { q, count = results.Count, results });
});

app.MapGet("/api/reports/daily", async (ShopDb db, Workload workload, HttpResponse response, CancellationToken ct) =>
{
    var rows = await workload.ReportAsync(db, ct);
    response.Headers[SelfClient.QueriesHeader] = "1";
    return Results.Ok(rows);
});

app.MapPost("/api/cart/{id:int}/items", async (int id, ShopDb db, Workload workload, HttpResponse response, CancellationToken ct) =>
{
    await workload.CartUpdateAsync(db, id, ct);
    response.Headers[SelfClient.QueriesHeader] = "1";
    return Results.Ok(new { cart = id });
});

// ---- the control panel behind the UI -------------------------------------------------

app.MapGet("/api/status", async (ShopDb db, Telemetry telemetry, LoadGenerator load, BackgroundJobs jobs,
    IConfiguration configuration, CancellationToken ct) =>
{
    var tables = await Status.TablesAsync(db, ct);
    return Results.Ok(new
    {
        database = db.Database.GetDbConnection().Database,
        host = db.Database.GetDbConnection().DataSource,
        momusUrl = configuration["MomusUrl"] ?? "http://localhost:4848",
        traffic = load.Mode,
        uptimeSeconds = (int)(DateTimeOffset.UtcNow - telemetry.StartedAt).TotalSeconds,
        operations = telemetry.Operations,
        queries = telemetry.Queries,
        errors = telemetry.Errors,
        tables,
        jobs = jobs.Running.Select(j => new { j.Id, j.Title, j.SecondsLeft }),
        scenarios = Workload.Catalogue,
        log = telemetry.Recent().Select(l => new { at = l.At, l.Level, l.Message }),
    });
});

app.MapPost("/api/traffic/{mode}", (string mode, LoadGenerator load) =>
    load.SetMode(mode) ? Results.Ok(new { traffic = load.Mode }) : Results.BadRequest(new { error = "off | light | heavy" }));

app.MapPost("/api/scenario/{id}", async (string id, Workload workload, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(new { message = await workload.RunAsync(id, ct) });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/scenario/{id}/stop", (string id, BackgroundJobs jobs) =>
    jobs.Stop(id) ? Results.Ok(new { stopped = id }) : Results.NotFound(new { error = $"'{id}' is not running." }));

app.MapPost("/api/reset", async (ShopDb db, ILoggerFactory loggers, Telemetry telemetry, CancellationToken ct) =>
{
    await Schema.ResetAsync(db, loggers.CreateLogger("Shop.Reset"), ct);
    telemetry.Log("info", "Database dropped and reseeded.");
    return Results.Ok(new { reseeded = true });
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
return;

// ---- startup -------------------------------------------------------------------------

static async Task StartupAsync(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shop.Startup");
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ShopDb>();

    // Compose waits for the database's health check, but a plain `dotnet run` does not, and a demo
    // that crashes because Postgres needed two more seconds teaches nothing.
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await Schema.EnsureAsync(db, logger);
            break;
        }
        catch (Exception ex) when (attempt < 30)
        {
            logger.LogWarning("Database not ready ({Message}); retrying in 2s [{Attempt}/30].", ex.Message, attempt);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
