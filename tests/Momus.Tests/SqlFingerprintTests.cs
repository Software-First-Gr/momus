using System.Text.Json;
using Momus.Core;

namespace Momus.Tests;

/// <summary>
/// The fingerprint is the join between Momus's two halves, so the test that matters is not a
/// hand-written expectation: it is a real statement an application sent, paired with the text the
/// database recorded for that same execution. Both halves of every pair in
/// <c>Fixtures/fingerprint-pairs-*.json</c> came out of a running server, captured by
/// <c>tools/FingerprintCapture</c>. Regenerate them with:
///
///   dotnet run --project tools/FingerprintCapture -- --provider postgres --connection "..." \
///     --out tests/Momus.Tests/Fixtures/fingerprint-pairs-postgres.json
/// </summary>
public class SqlFingerprintTests
{
    public static TheoryData<string, string, string> PostgresPairs => Load("postgres");
    public static TheoryData<string, string, string> SqlServerPairs => Load("sqlserver");

    [Theory]
    [MemberData(nameof(PostgresPairs))]
    public void App_and_pg_stat_statements_agree(string name, string app, string db)
    {
        AssertSameKey(name, app, db);
    }

    [Theory]
    [MemberData(nameof(SqlServerPairs))]
    public void App_and_dm_exec_sql_text_agree(string name, string app, string db)
    {
        AssertSameKey(name, app, db);
    }

    [Fact]
    public void Fixtures_cover_enough_shapes_to_be_worth_trusting()
    {
        Assert.True(PostgresPairs.Count() >= 10, $"Only {PostgresPairs.Count()} Postgres pairs.");
        Assert.True(SqlServerPairs.Count() >= 10, $"Only {SqlServerPairs.Count()} SQL Server pairs.");
    }

    private static void AssertSameKey(string name, string app, string db)
    {
        // The database records one statement; the application may have sent it inside a batch.
        var fromApp = SqlFingerprint.Split(app);
        var fromDb = SqlFingerprint.Analyze(db);

        Assert.True(fromApp.Any(s => s.Key == fromDb.Key),
            $"""
             {name}: no statement in the command matched what the database recorded.
               app  {string.Join(", ", fromApp.Select(s => s.Key))}  {app.ReplaceLineEndings(" ")}
               db   {fromDb.Key}  {db.ReplaceLineEndings(" ")}
               app normalized: {string.Join(" || ", fromApp.Select(s => s.Text))}
               db  normalized: {fromDb.Text}
             """);
    }

    // ---- normalization rules, stated directly ----------------------------------------

    [Fact]
    public void Parameter_markers_of_every_dialect_collapse_to_one_form()
    {
        var expected = SqlFingerprint.Compute("select * from t where a = ?");
        Assert.Equal(expected, SqlFingerprint.Compute("SELECT * FROM t WHERE a = @p0"));
        Assert.Equal(expected, SqlFingerprint.Compute("SELECT * FROM t WHERE a = $1"));
        Assert.Equal(expected, SqlFingerprint.Compute("SELECT * FROM t WHERE a = :p1"));
        Assert.Equal(expected, SqlFingerprint.Compute("SELECT * FROM t WHERE a = 42"));
        Assert.Equal(expected, SqlFingerprint.Compute("SELECT * FROM t WHERE a = 'literal'"));
    }

    [Fact]
    public void A_postgres_cast_is_an_operator_and_not_a_parameter_marker()
    {
        // ":float" looks exactly like the ":p1" marker one rule down, so a cast used to eat its
        // own second colon — which silently gave two different casts of one column the same key.
        Assert.NotEqual(
            SqlFingerprint.Compute("select count(*)::float from t"),
            SqlFingerprint.Compute("select count(*)::text from t"));

        Assert.Contains("::float", SqlFingerprint.Normalize("select count(*)::float from t"));
    }

    [Fact]
    public void Whitespace_and_keyword_case_do_not_change_the_key()
    {
        Assert.Equal(
            SqlFingerprint.Compute("SELECT a FROM t WHERE b = 1"),
            SqlFingerprint.Compute("select   a\n  from t\n  where b = 2"));
    }

    [Fact]
    public void Identifier_case_does_change_the_key()
    {
        // Two different tables on a case-sensitive server must not share a key.
        Assert.NotEqual(
            SqlFingerprint.Compute("SELECT a FROM Orders"),
            SqlFingerprint.Compute("SELECT a FROM orders"));
    }

    [Fact]
    public void In_lists_of_any_length_share_one_key()
    {
        var one = SqlFingerprint.Compute("SELECT a FROM t WHERE id IN ($1)");
        Assert.Equal(one, SqlFingerprint.Compute("SELECT a FROM t WHERE id IN ($1, $2, $3)"));
        Assert.Equal(one, SqlFingerprint.Compute("SELECT a FROM t WHERE id IN (1, 2, 3, 4, 5, 6, 7)"));
        Assert.Equal(one, SqlFingerprint.Compute("SELECT a FROM t WHERE id IN (@p0, @p1)"));
    }

    [Fact]
    public void Batches_of_values_share_one_key_with_a_single_insert()
    {
        var single = SqlFingerprint.Compute("INSERT INTO t (a, b) VALUES ($1, $2)");
        Assert.Equal(single, SqlFingerprint.Compute("INSERT INTO t (a, b) VALUES ($1, $2), ($3, $4)"));
        Assert.Equal(single, SqlFingerprint.Compute("INSERT INTO t (a, b) VALUES (1, 2), (3, 4), (5, 6)"));
    }

    [Fact]
    public void Comments_are_stripped_and_the_first_one_is_kept_as_a_call_site()
    {
        var plain = SqlFingerprint.Compute("SELECT a FROM t");

        var tagged = SqlFingerprint.Analyze("-- OrdersHandler.cs:42\n\nSELECT a FROM t");
        Assert.Equal(plain, tagged.Key);
        Assert.Equal("OrdersHandler.cs:42", tagged.Tag);

        var block = SqlFingerprint.Analyze("/* ImportJob */ SELECT a /* inline */ FROM t");
        Assert.Equal(plain, block.Key);
        Assert.Equal("ImportJob", block.Tag);
    }

    [Fact]
    public void A_question_mark_inside_a_literal_is_not_a_parameter()
    {
        // If the scanner treated the quotes naively these two would collide.
        Assert.Equal(
            SqlFingerprint.Compute("SELECT a FROM t WHERE b = 'why?'"),
            SqlFingerprint.Compute("SELECT a FROM t WHERE b = 'no'"));

        Assert.NotEqual(
            SqlFingerprint.Compute("SELECT a FROM t WHERE b = 'x' AND c = 'y'"),
            SqlFingerprint.Compute("SELECT a FROM t WHERE b = 'x'"));
    }

    [Fact]
    public void Different_statements_do_not_collide()
    {
        Assert.NotEqual(
            SqlFingerprint.Compute("SELECT a FROM orders WHERE id = $1"),
            SqlFingerprint.Compute("SELECT a FROM order_lines WHERE id = $1"));

        Assert.NotEqual(
            SqlFingerprint.Compute("SELECT a FROM t WHERE id = $1"),
            SqlFingerprint.Compute("UPDATE t SET a = $1 WHERE id = $2"));
    }

    [Fact]
    public void Empty_and_null_input_are_handled_rather_than_thrown_at()
    {
        Assert.Equal("", SqlFingerprint.Normalize(null));
        Assert.Equal(SqlFingerprint.Compute(""), SqlFingerprint.Compute(null));
        Assert.Equal(16, SqlFingerprint.Compute("SELECT 1").Length);
    }

    private static TheoryData<string, string, string> Load(string provider)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"fingerprint-pairs-{provider}.json");
        var data = new TheoryData<string, string, string>();
        if (!File.Exists(path)) return data;

        var fixture = JsonSerializer.Deserialize<FixtureFile>(File.ReadAllText(path))!;
        foreach (var pair in fixture.Pairs) data.Add(pair.Name, pair.App, pair.Db);
        return data;
    }

    private sealed record FixtureFile(string Provider, string ServerVersion, List<FixturePair> Pairs);

    private sealed record FixturePair(string Name, string App, string Db);
}
