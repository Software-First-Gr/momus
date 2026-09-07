using System.Text;
using static System.FormattableString;

namespace Momus.Server.Pages;

/// <summary>
/// Twenty-four hours of something, as one inline SVG. No chart library, no build step, and no axis
/// — the question a sparkline answers is "is this new, steady, or growing", and an axis does not
/// help with that.
/// </summary>
public static class Sparkline
{
    public static string Svg(IReadOnlyList<long>? values, int width = 120, int height = 24)
    {
        if (values is null || values.Count < 2 || values.All(v => v == 0)) return "";

        var max = (double)values.Max();
        var step = (double)width / (values.Count - 1);
        var points = new StringBuilder();

        for (var i = 0; i < values.Count; i++)
        {
            // One pixel of headroom top and bottom so a flat line at the maximum is still visible.
            var y = height - 1 - (values[i] / max * (height - 2));
            points.Append(Invariant($"{i * step:F1},{y:F1} "));
        }

        return Invariant(
            $"""
             <svg viewBox="0 0 {width} {height}" width="{width}" height="{height}"
                  preserveAspectRatio="none" aria-hidden="true" style="vertical-align:middle">
               <polyline points="{points.ToString().Trim()}" fill="none"
                         stroke="currentColor" stroke-width="1.5" stroke-linejoin="round" opacity="0.7" />
             </svg>
             """);
    }
}
