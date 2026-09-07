using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Momus.Tools.FingerprintCapture;

// Collects real (app text, statistics-view text) pairs for the SqlFingerprint tests.
//
//   dotnet run --project tools/FingerprintCapture -- \
//     --provider postgres --connection "Host=localhost;Port=55432;Username=postgres;Password=postgres;Database=shop" \
//     --out tests/Momus.Tests/Fixtures/fingerprint-pairs-postgres.json
//
// It creates three tables of its own, runs a set of EF Core query shapes one at a time, reads
// back what the database recorded for each, and drops the tables again. Nothing is hand-written:
// both halves of every pair come out of a running database, which is the only way to know that
// the fingerprint really is the same key on both sides.

var provider = Arg("--provider") ?? "postgres";
var connectionString = Arg("--connection") ?? throw new ArgumentException("--connection is required");
var output = Arg("--out") ?? "fingerprint-pairs.json";
var keep = args.Contains("--keep");

var options = new DbContextOptionsBuilder<FxDb>();
var capture = new CaptureInterceptor();
options.AddInterceptors(capture);

DatabaseProbe probe = provider switch
{
    "postgres" or "pg" => new PostgresProbe(),
    "sqlserver" or "mssql" => new SqlServerProbe(),
    _ => throw new ArgumentException($"Unknown provider '{provider}'."),
};
probe.Configure(options, connectionString);

await using var db = new FxDb(options.Options);
var connection = db.Database.GetDbConnection();
await connection.OpenAsync();

Console.WriteLine($"Connected to {provider} / {connection.Database}.");
await probe.CreateSchemaAsync(db);
await probe.PrepareAsync(connection);

var pairs = new List<Pair>();
foreach (var (name, run) in Shapes.All)
{
    capture.Clear();
    var before = await probe.SnapshotAsync(connection);

    try
    {
        await run(db);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {name}: skipped ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})");
        continue;
    }

    var appTexts = capture.Captured;
    var dbTexts = await probe.NewSinceAsync(connection, before);

    // One statement in, one statement out is the only case that pairs unambiguously.
    if (appTexts.Count != 1 || dbTexts.Count != 1)
    {
        Console.WriteLine($"  {name}: skipped ({appTexts.Count} app / {dbTexts.Count} db statements)");
        continue;
    }

    pairs.Add(new Pair(name, appTexts[0], dbTexts[0]));
    Console.WriteLine($"  {name}: captured");
}

if (!keep) await probe.DropSchemaAsync(db);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(
    new Fixture(provider, connection.ServerVersion, DateTimeOffset.UtcNow, pairs),
    new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"{pairs.Count} pairs written to {output}");
return pairs.Count == 0 ? 1 : 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

internal sealed record Pair(string Name, string App, string Db);

internal sealed record Fixture(string Provider, string ServerVersion, DateTimeOffset CapturedAt, List<Pair> Pairs);

/// <summary>Records the SQL EF Core actually sends, which is one half of every pair.</summary>
internal sealed class CaptureInterceptor : DbCommandInterceptor
{
    public List<string> Captured { get; } = [];

    public void Clear() => Captured.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result)
    {
        Captured.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        Captured.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData data, InterceptionResult<int> result)
    {
        Captured.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Captured.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData data, InterceptionResult<object> result)
    {
        Captured.Add(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData data, InterceptionResult<object> result, CancellationToken ct = default)
    {
        Captured.Add(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
