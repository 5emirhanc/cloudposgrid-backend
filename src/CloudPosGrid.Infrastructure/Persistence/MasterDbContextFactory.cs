using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudPosGrid.Infrastructure.Persistence;

/// <summary>`dotnet ef` migration komutları için tasarım-zamanı fabrikası (public şema).</summary>
public class MasterDbContextFactory : IDesignTimeDbContextFactory<MasterDbContext>
{
    public MasterDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("CLOUDPOSGRID_DB")
            ?? "Host=127.0.0.1;Port=5432;Database=cloudposgrid;Username=postgres";

        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__ef_migrations_history", "public"))
            .Options;

        return new MasterDbContext(options);
    }
}
