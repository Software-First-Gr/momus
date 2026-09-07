using Microsoft.EntityFrameworkCore;

namespace Momus.Tools.FingerprintCapture;

/// <summary>A throwaway schema, small enough to create and drop inside a capture run.</summary>
public sealed class FxDb(DbContextOptions<FxDb> options) : DbContext(options)
{
    public DbSet<FxProduct> Products => Set<FxProduct>();
    public DbSet<FxOrder> Orders => Set<FxOrder>();
    public DbSet<FxLine> Lines => Set<FxLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<FxProduct>().ToTable("fx_products");
        b.Entity<FxOrder>().ToTable("fx_orders");
        b.Entity<FxLine>().ToTable("fx_lines");
    }
}

public sealed class FxProduct
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
}

public sealed class FxOrder
{
    public int Id { get; set; }
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class FxLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
