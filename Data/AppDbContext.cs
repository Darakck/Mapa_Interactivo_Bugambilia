using Microsoft.EntityFrameworkCore;
using MapaInteractivoBugambilia.Models;

namespace MapaInteractivoBugambilia.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Lot> Lots => Set<Lot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Lot>(e =>
        {
            e.HasKey(x => x.Id);

            e.Property(x => x.ProjectKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Block).HasMaxLength(8).IsRequired();
            e.Property(x => x.DisplayCode).HasMaxLength(32).IsRequired();
            e.HasIndex(x => new { x.ProjectKey, x.DisplayCode }).IsUnique();

            e.Property(x => x.LotType).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);

            e.Property(x => x.AreaM2).HasPrecision(18, 2);
            e.Property(x => x.AreaV2).HasPrecision(18, 2);
            e.Property(x => x.X).HasPrecision(18, 6);
            e.Property(x => x.Y).HasPrecision(18, 6);
        });
    }
}