using Momus.Core;
using Momus.Postgres;
using Momus.Postgres.Checks;
using Momus.Tests.Fakes;

namespace Momus.Tests;

/// <summary>
/// Sessions blocked on a lock, and who is holding it. The holder's last statement is the part that
/// matters most: it is how an application-side insight tells its own transaction from somebody else's.
/// </summary>
public class LockWaitsTests
{
    [Theory]
    [InlineData(2, null)]
    [InlineData(5, Severity.Medium)]
    [InlineData(59, Severity.Medium)]
    [InlineData(60, Severity.High)]
    public void A_wait_is_a_finding_from_five_seconds_and_High_from_a_minute(long seconds, Severity? expected) =>
        Assert.Equal(expected, PostgresThresholds.LockWaitSeverity(seconds));

    [Fact]
    public async Task A_session_blocked_by_one_idle_in_transaction_names_the_holder_and_what_it_last_ran()
    {
        var connection = new FakeDataConnection().When("wait_event_type = 'Lock'", Row(
            ("pid", 51L), ("usename", "app"), ("application_name", "Shop.Api"), ("wait_event", "transactionid"),
            ("waiting_secs", 42L),
            ("query", "UPDATE carts SET total = total + $1 WHERE id = $2"),
            ("full_query", "UPDATE carts SET total = total + $1 WHERE id = $2"),
            ("blocker_count", 1L), ("blocker_pid", 44L), ("blocker_state", "idle in transaction"),
            ("blocker_state_secs", 300L),
            ("blocker_query", "UPDATE carts SET updated_at = $1 WHERE id = $2"),
            ("blocker_full_query", "UPDATE carts SET updated_at = $1 WHERE id = $2")));
        await connection.OpenAsync();

        var finding = Assert.Single(await new LockWaitsCheck().RunAsync(connection, CancellationToken.None));

        Assert.Equal("Session 51 has waited 42 s for a lock held by session 44", finding.Title);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Contains("idle in transaction", finding.Detail);
        Assert.Equal([Subject.ForSession(51), Subject.ForSession(44)], finding.Subjects);
        Assert.Equal(
            SqlFingerprint.Compute("UPDATE carts SET updated_at = @p0 WHERE id = @p1"),
            finding.Evidence["blocker_query_fingerprint"]);
    }

    [Fact]
    public async Task Nobody_waiting_is_healthy()
    {
        var connection = new FakeDataConnection();
        await connection.OpenAsync();

        Assert.Empty(await new LockWaitsCheck().RunAsync(connection, CancellationToken.None));
    }

    private static Dictionary<string, object?> Row(params (string Name, object? Value)[] cells)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in cells) row[name] = value;
        return row;
    }
}
