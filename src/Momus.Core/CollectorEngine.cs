using System.Diagnostics;

namespace Momus.Core;

/// <summary>
/// Runs every check of a scan target over one connection. Checks are isolated:
/// a throwing check is recorded as a failed <see cref="CheckResult"/> and the scan continues.
/// </summary>
public sealed class CollectorEngine
{
    public async Task<ScanReport> ScanAsync(IScanTarget target, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();

        await using var connection = target.CreateConnection();
        await connection.OpenAsync(ct);

        var info = await target.GetTargetInfoAsync(connection, ct);

        var results = new List<CheckResult>(target.Checks.Count);
        foreach (var check in target.Checks)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            try
            {
                var findings = await check.RunAsync(connection, ct);
                results.Add(new CheckResult
                {
                    CheckId = check.Id,
                    Title = check.Title,
                    Category = check.Category,
                    Succeeded = true,
                    Duration = sw.Elapsed,
                    Findings = findings,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult
                {
                    CheckId = check.Id,
                    Title = check.Title,
                    Category = check.Category,
                    Succeeded = false,
                    Duration = sw.Elapsed,
                    Error = ex.Message,
                });
            }
        }

        return new ScanReport
        {
            StartedAt = startedAt,
            Duration = total.Elapsed,
            Target = info,
            Checks = results,
        };
    }
}
