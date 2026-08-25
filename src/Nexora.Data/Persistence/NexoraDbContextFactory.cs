using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nexora.Data.Persistence;

public sealed class NexoraDbContextFactory : IDesignTimeDbContextFactory<NexoraDbContext>
{
    public NexoraDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=nexora;Username=nexora";
        var options = new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(connectionString).Options;
        return new NexoraDbContext(options);
    }
}
