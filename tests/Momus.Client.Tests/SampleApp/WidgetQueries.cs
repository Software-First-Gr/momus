using Microsoft.EntityFrameworkCore;

namespace SampleApp;

/// <summary>
/// Stands in for the application under observation, and deliberately lives outside
/// <c>Momus.Client.*</c>: the call-site walker skips its own namespace and its adapter packages,
/// so a test whose "application code" sat inside that tree could never prove the walker names
/// application code rather than the framework above it.
/// </summary>
public static class WidgetQueries
{
    public static Task<Widget?> LoadOneAsync(WidgetDb db, int id) =>
        db.Widgets.Where(w => w.Id == id).FirstOrDefaultAsync();

    public static Task<List<Widget>> LoadAllAsync(WidgetDb db) => db.Widgets.ToListAsync();

    /// <summary>The shape the whole product exists to find: one statement per row of another.</summary>
    public static async Task LoadEachAsync(WidgetDb db, IEnumerable<int> ids)
    {
        foreach (var id in ids) await LoadOneAsync(db, id);
    }
}
