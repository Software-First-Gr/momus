using Microsoft.EntityFrameworkCore;

namespace Shop.Api;

/// <summary>
/// A deliberately ordinary shop schema. The interesting part is what it is missing: no index on
/// <c>order_lines.order_id</c>, a never-queried index on <c>order_lines.price</c>, and enough rows
/// that both facts show up in the database's own statistics views.
/// </summary>
public sealed class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>(e =>
        {
            e.ToTable("customers");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Email).HasColumnName("email");
        });

        b.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Sku).HasColumnName("sku");
            e.Property(x => x.Category).HasColumnName("category");
            e.Property(x => x.Price).HasColumnName("price");
        });

        b.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<OrderLine>(e =>
        {
            e.ToTable("order_lines");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrderId).HasColumnName("order_id");
            e.Property(x => x.ProductId).HasColumnName("product_id");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.Price).HasColumnName("price");
        });

        b.Entity<Cart>(e =>
        {
            e.ToTable("carts");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CustomerId).HasColumnName("customer_id");
            e.Property(x => x.Total).HasColumnName("total");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Actor).HasColumnName("actor");
            e.Property(x => x.Action).HasColumnName("action");
        });
    }
}

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal Price { get; set; }
}

public sealed class Cart
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
}
