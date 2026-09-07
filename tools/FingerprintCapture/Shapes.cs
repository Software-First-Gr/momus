using Microsoft.EntityFrameworkCore;

namespace Momus.Tools.FingerprintCapture;

/// <summary>
/// The query shapes worth proving. Each one is a thing EF Core does to SQL that could break the
/// join with the statistics view: its own aliases, an IN list, a batch of VALUES, a tag comment,
/// pagination parameters, a bulk update. One statement per shape, so pairing stays unambiguous.
/// </summary>
public static class Shapes
{
    public static readonly (string Name, Func<FxDb, Task> Run)[] All =
    [
        ("find_by_key", async db =>
            await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == 1)),

        ("filter_order_take", async db =>
            await db.Products.AsNoTracking()
                .Where(p => p.Price > 10)
                .OrderBy(p => p.Name)
                .Take(20)
                .ToListAsync()),

        ("in_list", async db =>
        {
            int[] ids = [1, 2, 3, 4, 5];
            await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync();
        }),

        ("like_contains", async db =>
            await db.Products.AsNoTracking().Where(p => p.Name.Contains("widget")).ToListAsync()),

        ("like_starts_with", async db =>
            await db.Products.AsNoTracking().Where(p => p.Category.StartsWith("too")).ToListAsync()),

        ("lower_like", async db =>
            await db.Products.AsNoTracking().Where(p => p.Name.ToLower().Contains("gizmo")).ToListAsync()),

        ("join", async db =>
            await (from l in db.Lines.AsNoTracking()
                   join p in db.Products.AsNoTracking() on l.ProductId equals p.Id
                   where l.Quantity > 1
                   select new { l.Id, p.Name, p.Price }).ToListAsync()),

        ("group_by_count", async db =>
            await db.Orders.AsNoTracking()
                .GroupBy(o => o.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync()),

        ("count_scalar", async db =>
            await db.Products.AsNoTracking().CountAsync(p => p.Price > 5)),

        ("any_exists", async db =>
            await db.Products.AsNoTracking().AnyAsync(p => p.Price > 100)),

        ("max_aggregate", async db =>
            await db.Products.AsNoTracking().MaxAsync(p => p.Price)),

        ("date_filter", async db =>
        {
            var since = DateTime.UtcNow.AddDays(-7);
            await db.Orders.AsNoTracking().Where(o => o.CreatedAt > since).ToListAsync();
        }),

        ("tagged_call_site", async db =>
            await db.Orders.AsNoTracking()
                .TagWith("OrdersHandler.cs:42")
                .Where(o => o.Status == "paid")
                .ToListAsync()),

        ("pagination", async db =>
            await db.Products.AsNoTracking()
                .OrderBy(p => p.Id)
                .Skip(20)
                .Take(10)
                .ToListAsync()),

        ("correlated_subquery", async db =>
            await db.Products.AsNoTracking()
                .Where(p => db.Lines.Any(l => l.ProductId == p.Id))
                .ToListAsync()),

        ("distinct_projection", async db =>
            await db.Products.AsNoTracking().Select(p => p.Category).Distinct().ToListAsync()),

        ("order_by_multiple", async db =>
            await db.Orders.AsNoTracking()
                .OrderByDescending(o => o.Total)
                .ThenBy(o => o.CreatedAt)
                .Take(5)
                .ToListAsync()),

        ("insert_single", async db =>
        {
            db.Products.Add(new FxProduct { Name = "one", Category = "tools", Price = 1.5m });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }),

        ("insert_batch", async db =>
        {
            db.Products.AddRange(
                new FxProduct { Name = "a", Category = "tools", Price = 1m },
                new FxProduct { Name = "b", Category = "garden", Price = 2m },
                new FxProduct { Name = "c", Category = "kitchen", Price = 3m });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }),

        ("execute_update", async db =>
            await db.Orders.Where(o => o.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Total, o => o.Total + 1))),

        ("execute_delete", async db =>
            await db.Lines.Where(l => l.Quantity < 0).ExecuteDeleteAsync()),
    ];
}
