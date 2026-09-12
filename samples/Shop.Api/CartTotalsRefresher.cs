using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

/// <summary>
/// The regression the demo ships in its slow build (<c>SHOP_SLOW_BUILD=true</c>): a new background
/// job that recalculates cart totals under a table lock, so every <c>POST /api/cart/{id}/items</c>
/// queues behind it.
/// </summary>
/// <remarks>
/// The cart update's SQL does not change by a character, and that is the point. <c>regression</c>
/// compares one statement across two versions; a statement whose text changed is, to both the
/// database and Momus, a different statement. What changed is what it has to wait for — which is
/// how most real regressions that survive code review look.
/// </remarks>
public sealed class CartTotalsRefresher(IServiceScopeFactory scopes, ILogger<CartTotalsRefresher> logger)
    : BackgroundService
{
#if SLOW_BUILD
    public static readonly bool Enabled = true;
#else
    public static readonly bool Enabled = false;
#endif

    /// <summary>How long each pass holds the lock, and how long it lets go for in between.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(80), Gap = TimeSpan.FromMilliseconds(20);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) return;

        logger.LogWarning("Slow build: cart totals are refreshed under a lock on carts, every {Ms} ms.",
            (Hold + Gap).TotalMilliseconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ShopDb>();

                await using var tx = await db.Database.BeginTransactionAsync(ct);
                // SHARE conflicts with the ROW EXCLUSIVE lock every UPDATE takes.
                await db.Database.ExecuteSqlRawAsync("LOCK TABLE carts IN SHARE MODE", ct);
                await Task.Delay(Hold, ct); // "recalculating"
                await tx.CommitAsync(ct);

                await Task.Delay(Gap, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Cart totals refresh failed: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
