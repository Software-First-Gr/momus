using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

/// <summary>
/// Creates and fills the demo schema with raw SQL. Raw SQL rather than EF migrations because the
/// point of this app is which indexes are missing, and generating 240,000 order lines server-side
/// takes a second where EF would take minutes.
/// </summary>
public static class Schema
{
    public const int Customers = 5_000;
    public const int Products = 60_000;
    public const int Orders = 40_000;
    public const int LinesPerOrder = 6;
    public const int AuditEntries = 150_000;

    public static async Task EnsureAsync(ShopDb db, ILogger logger, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS customers (
                id serial PRIMARY KEY,
                name text NOT NULL,
                email text NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now());

            CREATE TABLE IF NOT EXISTS products (
                id serial PRIMARY KEY,
                name text NOT NULL,
                sku text NOT NULL,
                category text NOT NULL,
                price numeric(10,2) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now());

            CREATE TABLE IF NOT EXISTS orders (
                id serial PRIMARY KEY,
                customer_id int NOT NULL,
                status text NOT NULL,
                total numeric(10,2) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now());

            -- No index on order_id. That is the whole point: the N+1 endpoint scans this table
            -- once per line, and Momus should say so from the database side.
            CREATE TABLE IF NOT EXISTS order_lines (
                id serial PRIMARY KEY,
                order_id int NOT NULL,
                product_id int NOT NULL,
                quantity int NOT NULL,
                price numeric(10,2) NOT NULL);

            CREATE TABLE IF NOT EXISTS carts (
                id serial PRIMARY KEY,
                customer_id int NOT NULL,
                total numeric(10,2) NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now());

            CREATE TABLE IF NOT EXISTS audit_log (
                id bigserial PRIMARY KEY,
                at timestamptz NOT NULL,
                actor text NOT NULL,
                action text NOT NULL);

            -- Two indexes nothing ever reads: write cost and disk for no benefit.
            CREATE INDEX IF NOT EXISTS ix_order_lines_price ON order_lines (price);
            CREATE INDEX IF NOT EXISTS ix_audit_log_actor ON audit_log (actor);
            """, ct);

        var seeded = await db.Products.AnyAsync(ct);
        if (seeded)
        {
            logger.LogInformation("Schema ready, already seeded ({Elapsed:N0} ms).", sw.ElapsedMilliseconds);
            return;
        }

        logger.LogInformation("Seeding demo data — this takes a few seconds, once.");

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO customers (name, email)
            SELECT 'Customer ' || g, 'customer' || g || '@example.com'
            FROM generate_series(1, {Customers}) g;

            INSERT INTO products (name, sku, category, price)
            SELECT 'Product ' || g || ' ' || (ARRAY['widget','gizmo','sprocket','bracket'])[1 + (g % 4)],
                   'SKU-' || lpad(g::text, 7, '0'),
                   (ARRAY['tools','garden','kitchen','office'])[1 + (g % 4)],
                   round((random() * 200 + 1)::numeric, 2)
            FROM generate_series(1, {Products}) g;

            INSERT INTO orders (customer_id, status, total, created_at)
            SELECT 1 + (random() * {Customers - 1})::int,
                   (ARRAY['placed','paid','shipped','cancelled'])[1 + (g % 4)],
                   round((random() * 500 + 10)::numeric, 2),
                   now() - (random() * interval '90 days')
            FROM generate_series(1, {Orders}) g;

            INSERT INTO order_lines (order_id, product_id, quantity, price)
            SELECT o, 1 + (random() * {Products - 1})::int, 1 + (random() * 4)::int,
                   round((random() * 200 + 1)::numeric, 2)
            FROM generate_series(1, {Orders}) o, generate_series(1, {LinesPerOrder}) l;

            INSERT INTO carts (customer_id, total)
            SELECT g, round((random() * 300)::numeric, 2) FROM generate_series(1, {Customers}) g;

            INSERT INTO audit_log (at, actor, action)
            SELECT now() - (random() * interval '30 days'),
                   'user' || (1 + (random() * 200)::int),
                   (ARRAY['login','checkout','refund','view','export'])[1 + (g % 5)]
            FROM generate_series(1, {AuditEntries}) g;

            ANALYZE;
            """, ct);

        logger.LogInformation("Seeded {Orders:N0} orders / {Lines:N0} order lines in {Elapsed:N0} ms.",
            Orders, Orders * LinesPerOrder, sw.ElapsedMilliseconds);
    }

    /// <summary>Drops everything and seeds again. Wired to the Reset button in the UI.</summary>
    public static async Task ResetAsync(ShopDb db, ILogger logger, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync(
            "DROP TABLE IF EXISTS order_lines, orders, carts, products, customers, audit_log CASCADE;", ct);
        await EnsureAsync(db, logger, ct);
    }
}
