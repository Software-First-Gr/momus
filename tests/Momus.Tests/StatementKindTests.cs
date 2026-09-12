using Momus.Core;

namespace Momus.Tests;

public class StatementKindTests
{
    [Theory]
    [InlineData("DISCARD all", true)]
    [InlineData("SET application_name = ?", true)]
    [InlineData("BEGIN", true)]
    [InlineData("start transaction isolation level serializable", true)]
    [InlineData("commit", true)]
    [InlineData("release savepoint p1", true)]
    [InlineData("  rollback", true)]
    [InlineData("select * from settings", false)]
    [InlineData("select ... from releases where id = ?", false)]
    [InlineData("update endpoints set hits = hits + ?", false)]
    [InlineData("SET @total = (SELECT sum(total) FROM orders)", false)]
    [InlineData("", false)]
    public void Session_and_transaction_bookkeeping_is_told_apart_from_queries(string sql, bool expected) =>
        Assert.Equal(expected, StatementKind.IsBookkeeping(sql));

    [Fact]
    public void Momus_recognises_its_own_statements_when_the_database_hands_them_back()
    {
        // What Momus sends, and what pg_stat_statements stores for it after normalizing the literal.
        Db.RememberOwn("SELECT count(*) FROM pg_extension WHERE extname = 'pg_stat_statements'");

        Assert.True(Db.IsOwn(SqlFingerprint.Compute("SELECT count(*) FROM pg_extension WHERE extname = $1")));
        Assert.False(Db.IsOwn(SqlFingerprint.Compute("SELECT count(*) FROM orders WHERE id = $1")));
    }
}
