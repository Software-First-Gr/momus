using Microsoft.EntityFrameworkCore;

namespace SampleApp;

public sealed class Widget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class WidgetDb(DbContextOptions<WidgetDb> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
}
