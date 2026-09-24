using Microsoft.Extensions.Logging.Abstractions;
using Momus.Core.Ingest;
using Momus.Server;
using Momus.Server.Store;

namespace Momus.Tests;

/// <summary>
/// An application's statements and the database's own ranking meet on the target id. The client
/// names a database by the slug of its name; a target from the environment has the id its name
/// gave it. With a server on another machine the client shares no connection string, and the two
/// used to join only if the names happened to slug alike — a target named "Staging DB" for the
/// database "atlas-app-stage" joined nothing, silently.
/// </summary>
public class TargetMatchTests
{
    [Fact]
    public void A_target_known_by_its_id_is_left_as_it_came()
    {
        var batch = Batch("shop", "postgres", "shop");

        var (result, renamed) = TargetMatch.Resolve(batch, [Target("shop", "postgres", "Host=db;Database=shop")]);

        Assert.Empty(renamed);
        Assert.Same(batch, result);
    }

    [Fact]
    public void A_database_is_matched_by_the_name_its_last_scan_reported()
    {
        var (result, renamed) = TargetMatch.Resolve(
            Batch("atlas-app-stage", "sqlserver", "atlas-app-stage"),
            [Target("staging-db", "sqlserver", "Server=10.0.0.1;User Id=momus", scanned: "atlas-app-stage")]);

        Assert.Equal("staging-db", renamed["atlas-app-stage"]);
        Assert.Equal("staging-db", Assert.Single(result.Targets).Id);
        Assert.Equal("staging-db", Assert.Single(result.Queries).Target);
    }

    [Theory]
    [InlineData("Server=10.0.0.1;Database=atlas-app-stage;User Id=momus")]
    [InlineData("Data Source=10.0.0.1;Initial Catalog=atlas-app-stage;User ID=momus")]
    public void Before_its_first_scan_a_database_is_matched_by_what_its_connection_string_names(string connectionString)
    {
        var (_, renamed) = TargetMatch.Resolve(
            Batch("atlas-app-stage", "sqlserver", "atlas-app-stage"),
            [Target("staging-db", "sqlserver", connectionString)]);

        Assert.Equal("staging-db", renamed["atlas-app-stage"]);
    }

    [Fact]
    public void The_spellings_of_one_engine_match_each_other()
    {
        var (_, renamed) = TargetMatch.Resolve(
            Batch("atlas-app-stage", "sqlserver", "atlas-app-stage"),
            [Target("staging-db", "mssql", "Server=x;Database=atlas-app-stage")]);

        Assert.Equal("staging-db", renamed["atlas-app-stage"]);
    }

    [Fact]
    public void The_same_name_on_another_engine_is_not_the_same_database()
    {
        var (_, renamed) = TargetMatch.Resolve(
            Batch("shop", "sqlserver", "shop"),
            [Target("reporting", "postgres", "Host=db;Database=shop")]);

        Assert.Empty(renamed);
    }

    [Fact]
    public void Two_targets_with_the_database_name_are_a_guess_and_not_made()
    {
        // Staging and demo on two servers, each with its own "shop": which one the application
        // uses is exactly what the name cannot say.
        var (_, renamed) = TargetMatch.Resolve(
            Batch("shop-app", "sqlserver", "shop"),
            [
                Target("staging", "sqlserver", "Server=a;Database=shop"),
                Target("demo", "sqlserver", "Server=b;Database=shop"),
            ]);

        Assert.Empty(renamed);
    }

    [Fact]
    public async Task Through_the_handler_the_statements_land_on_the_scanned_target_and_no_second_target_appears()
    {
        await using var store = await MomusStore.InMemoryAsync();
        await store.UpsertTargetAsync(Target("staging-db", "sqlserver",
            "Server=10.0.0.1;Database=atlas-app-stage;User Id=momus;Password=x"));
        using var scheduler = new ScanScheduler(store, new ServerOptions(), new ScanTargetFactory(),
            NullLogger<ScanScheduler>.Instance);
        var handler = new IngestHandler(store, scheduler, NullLogger<IngestHandler>.Instance);

        // Even with a connection string shared: the database is already scanned, under another id.
        var batch = Batch("atlas-app-stage", "sqlserver", "atlas-app-stage") with
        {
            Targets = [new IngestTarget("atlas-app-stage", "sqlserver", "atlas-app-stage", "Server=localhost;Database=atlas-app-stage")],
        };
        await handler.HandleAsync(batch, CancellationToken.None);

        var stat = Assert.Single(await store.AppQueryStatsAsync(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal("staging-db", stat.TargetId);
        Assert.Equal("staging-db", Assert.Single(await store.TargetsAsync()).Id);
    }

    // ---- fixtures ----------------------------------------------------------------------

    private static IngestBatch Batch(string targetId, string provider, string database) => new()
    {
        App = new IngestApp("Atlas.Server", "1.0.0+build.3e45f2a750a8", "web-01:1", "Staging"),
        Window = new IngestWindow(DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow),
        Targets = [new IngestTarget(targetId, provider, database, null)],
        Queries =
        [
            new IngestQuery("9f3a1c77d02b4e10", targetId, "GET /office", "OfficeQueries.cs:12",
                "select ... from positions where id = ?", 10, new Timing(18, 4, [0, 10, 0, 0, 0, 0, 0, 0]), 10, 1, 0),
        ],
    };

    private static StoredTarget Target(string id, string provider, string connectionString, string? scanned = null) => new()
    {
        Id = id, Name = id, Provider = provider, ConnectionString = connectionString, Source = "env",
        DatabaseName = scanned,
    };
}
