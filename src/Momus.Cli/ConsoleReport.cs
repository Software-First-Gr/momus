using Momus.Core;

namespace Momus.Cli;

/// <summary>Renders a scan report as a colored, human-first console summary.</summary>
public static class ConsoleReport
{
    public static void Render(ScanReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"  Momus scan — {report.Target.Provider} / {report.Target.DatabaseName}");
        Console.WriteLine($"  {report.Target.ServerVersion}");
        Console.WriteLine($"  {report.StartedAt:u} · {report.Duration.TotalSeconds:N1}s · " +
                          $"{report.Checks.Count} checks");
        Console.WriteLine(new string('─', 72));

        var findings = report.Findings.ToList();
        if (findings.Count == 0)
        {
            WriteColored(ConsoleColor.Green, "  ✓ No findings. Either the database is healthy or it hasn't seen real traffic yet.");
            Console.WriteLine();
        }

        foreach (var group in findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            Console.WriteLine();
            WriteColored(ColorFor(group.Key), $"  {group.Key.ToString().ToUpperInvariant()} ({group.Count()})");
            Console.WriteLine();
            foreach (var finding in group)
            {
                WriteColored(ColorFor(group.Key), "  ● ");
                Console.WriteLine(finding.Title);
                Console.WriteLine($"    {finding.Detail}");
                if (finding.Recommendation is not null)
                {
                    WriteColored(ConsoleColor.DarkGray, $"    → {finding.Recommendation}");
                    Console.WriteLine();
                }
            }
        }

        var failed = report.Checks.Where(c => !c.Succeeded).ToList();
        if (failed.Count > 0)
        {
            Console.WriteLine();
            WriteColored(ConsoleColor.DarkYellow, $"  {failed.Count} check(s) could not run:");
            Console.WriteLine();
            foreach (var check in failed)
            {
                Console.WriteLine($"  ✗ {check.Title}: {check.Error}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('─', 72));
        Console.WriteLine(
            $"  Summary: {Count(Severity.Critical)} critical · {Count(Severity.High)} high · " +
            $"{Count(Severity.Medium)} medium · {Count(Severity.Low)} low · {Count(Severity.Info)} info");
        Console.WriteLine();
        return;

        int Count(Severity s) => findings.Count(f => f.Severity == s);
    }

    private static ConsoleColor ColorFor(Severity severity) => severity switch
    {
        Severity.Critical => ConsoleColor.Magenta,
        Severity.High => ConsoleColor.Red,
        Severity.Medium => ConsoleColor.Yellow,
        Severity.Low => ConsoleColor.DarkYellow,
        _ => ConsoleColor.Cyan,
    };

    private static void WriteColored(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
