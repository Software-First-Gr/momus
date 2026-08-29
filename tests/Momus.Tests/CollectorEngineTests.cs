using System.Data.Common;
using Momus.Core;
using Xunit;

namespace Momus.Tests;

public class CollectorEngineTests
{
    [Fact]
    public async Task Scan_runs_all_checks_and_aggregates_findings()
    {
        var target = new FakeTarget(
            new FakeCheck("a", findings: [MakeFinding("a", Severity.High)]),
            new FakeCheck("b", findings: [MakeFinding("b", Severity.Info), MakeFinding("b", Severity.Low)]));

        var report = await new CollectorEngine().ScanAsync(target);

        Assert.Equal(2, report.Checks.Count);
        Assert.All(report.Checks, c => Assert.True(c.Succeeded));
        Assert.Equal(3, report.Findings.Count());
        Assert.Equal(1, report.CountAtLeast(Severity.High));
    }

    [Fact]
    public async Task A_throwing_check_is_isolated_and_does_not_abort_the_scan()
    {
        var target = new FakeTarget(
            new FakeCheck("boom", exception: new InvalidOperationException("permission denied for pg_stat_statements")),
            new FakeCheck("ok", findings: [MakeFinding("ok", Severity.Medium)]));

        var report = await new CollectorEngine().ScanAsync(target);

        var boom = Assert.Single(report.Checks, c => c.CheckId == "boom");
        Assert.False(boom.Succeeded);
        Assert.Contains("permission denied", boom.Error);
        Assert.Empty(boom.Findings);

        var ok = Assert.Single(report.Checks, c => c.CheckId == "ok");
        Assert.True(ok.Succeeded);
        Assert.Single(ok.Findings);
    }

    [Fact]
    public async Task Findings_are_ordered_by_severity_descending()
    {
        var target = new FakeTarget(
            new FakeCheck("a", findings:
            [
                MakeFinding("a", Severity.Low),
                MakeFinding("a", Severity.Critical),
                MakeFinding("a", Severity.Medium),
            ]));

        var report = await new CollectorEngine().ScanAsync(target);

        var severities = report.Findings.Select(f => f.Severity).ToList();
        Assert.Equal([Severity.Critical, Severity.Medium, Severity.Low], severities);
    }

    [Fact]
    public async Task Report_carries_target_info()
    {
        var report = await new CollectorEngine().ScanAsync(new FakeTarget());

        Assert.Equal("fake", report.Target.Provider);
        Assert.Equal("testdb", report.Target.DatabaseName);
    }

    private static Finding MakeFinding(string checkId, Severity severity) => new()
    {
        CheckId = checkId,
        Severity = severity,
        Title = $"{checkId}-{severity}",
        Detail = "detail",
    };

    private sealed class FakeCheck(string id, IReadOnlyList<Finding>? findings = null, Exception? exception = null)
        : IDiagnosticCheck
    {
        public string Id => id;
        public string Title => id;
        public string Category => "test";

        public Task<IReadOnlyList<Finding>> RunAsync(DbConnection connection, CancellationToken ct) =>
            exception is not null
                ? Task.FromException<IReadOnlyList<Finding>>(exception)
                : Task.FromResult(findings ?? []);
    }

    private sealed class FakeTarget(params IDiagnosticCheck[] checks) : IScanTarget
    {
        public string Provider => "fake";
        public IReadOnlyList<IDiagnosticCheck> Checks => checks;
        public DbConnection CreateConnection() => new FakeConnection();

        public Task<TargetInfo> GetTargetInfoAsync(DbConnection connection, CancellationToken ct) =>
            Task.FromResult(new TargetInfo { Provider = "fake", DatabaseName = "testdb" });
    }
}
