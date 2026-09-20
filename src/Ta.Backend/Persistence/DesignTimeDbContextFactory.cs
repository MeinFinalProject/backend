using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ta.Backend.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BackendDbContext>
{
    public BackendDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder().AddUserSecrets<BackendDbContext>().AddEnvironmentVariables().Build();
        var connection = config.GetConnectionString("Migration") ?? config.GetConnectionString("Backend")
            ?? "Host=localhost;Database=ta_backend_dev;Username=ta_backend_owner";
        return new BackendDbContext(new DbContextOptionsBuilder<BackendDbContext>().UseNpgsql(connection,
            pg => pg.MigrationsHistoryTable("ef_migration_history")).Options);
    }
}
