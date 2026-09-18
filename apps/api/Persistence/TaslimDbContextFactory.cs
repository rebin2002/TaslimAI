using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Taslim.Api.Persistence;

public sealed class TaslimDbContextFactory : IDesignTimeDbContextFactory<TaslimDbContext>
{
    public TaslimDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? configuration["ConnectionStrings__Postgres"]
            ?? configuration["DATABASE_URL"]
            ?? "Host=localhost;Port=5432;Database=taslim;Username=taslim;Password=change-me";
        var options = new DbContextOptionsBuilder<TaslimDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new TaslimDbContext(options);
    }
}
