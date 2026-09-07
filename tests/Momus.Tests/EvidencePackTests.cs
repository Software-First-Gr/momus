using Momus.Core;
using Momus.Core.Insights;
using Momus.Server.Insights;
using Momus.Server.Store;

namespace Momus.Tests;

/// <summary>
/// The evidence pack is how most of what Momus finds actually reaches a fix: pasted into a coding
/// agent. So it has to carry both halves' numbers and the statement, and nothing else.
/// </summary>
public class EvidencePackTests
{
    [Fact]
    public void A_pack_carries_both_sides_the_statement_and_the_versions()
    {
        var markdown = EvidencePack.Render(Insight(), Target(), App(), "0.3.0");

        Assert.Contains("## GET /orders/{id} runs one statement ×6 per call", markdown);
        Assert.Contains("`n_plus_one`", markdown);
        Assert.Contains("**What to try:**", markdown);

        Assert.Contains("```sql", markdown);
        Assert.Contains("select ... from products where id = ?", markdown);

        Assert.Contains("| call site | OrdersHandler.cs:42 |", markdown);
        Assert.Contains("| repeats per operation | 6 |", markdown);

        Assert.Contains("`query:9f3a`", markdown);
        Assert.Contains("Database `shop` (postgres/shop_db), PostgreSQL 18.4", markdown);
        Assert.Contains("Application `Shop.Api` 1.5.0 in Production", markdown);
        Assert.Contains("Momus 0.3.0", markdown);
    }

    [Fact]
    public void The_statement_is_shown_as_normalized_and_said_to_be()
    {
        // Someone about to paste this into an agent deserves to know no literal is in it.
        var markdown = EvidencePack.Render(Insight(), Target(), App(), "0.3.0");

        Assert.Contains("No value from your database or your users appears in this text", markdown);
    }

    [Fact]
    public void An_insight_with_unreadable_evidence_still_produces_a_pack()
    {
        var markdown = EvidencePack.Render(
            Insight() with { EvidenceJson = "not json at all" }, null, null, "0.3.0");

        Assert.Contains("## GET /orders/{id} runs one statement ×6 per call", markdown);
        Assert.DoesNotContain("```sql", markdown);
    }

    private static StoredInsight Insight() => new()
    {
        Kind = "n_plus_one",
        IdentityKey = "n_plus_one|query:9f3a",
        Severity = Severity.High,
        Title = "GET /orders/{id} runs one statement ×6 per call",
        Detail = "The same statement runs up to 6 times inside a single GET /orders/{id}.",
        Recommendation = "Fetch them together instead of one at a time.",
        EvidenceJson = """
            {"operation":"GET /orders/{id}","call_site":"OrdersHandler.cs:42",
             "repeats_per_operation":6,"statement":"select ... from products where id = ?"}
            """,
        Subjects = [Subject.ForQuery("9f3a")],
        FirstSeen = DateTimeOffset.UtcNow.AddHours(-4),
        LastSeen = DateTimeOffset.UtcNow,
        SeenCount = 12,
    };

    private static StoredTarget Target() => new()
    {
        Id = "shop", Name = "shop", Provider = "postgres",
        ConnectionString = "Host=localhost;Password=hunter2", Source = "cli",
        DatabaseName = "shop_db",
        ServerVersion = "PostgreSQL 18.4 on aarch64-unknown-linux-musl, compiled by gcc",
    };

    private static StoredApp App() => new()
    {
        Id = "shop-api", Name = "Shop.Api", Version = "1.5.0", Environment = "Production",
        FirstSeen = DateTimeOffset.UtcNow.AddDays(-2), LastSeen = DateTimeOffset.UtcNow,
        InstanceCount = 2,
    };

    [Fact]
    public void A_pack_never_carries_a_connection_string()
    {
        Assert.DoesNotContain("hunter2", EvidencePack.Render(Insight(), Target(), App(), "0.3.0"));
    }
}
