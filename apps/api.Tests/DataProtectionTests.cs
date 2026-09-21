using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taslim.Api.Persistence;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DataProtectionTests
{
    [Fact]
    public void A_new_service_provider_can_unprotect_data_from_a_previous_instance()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        EnsureDatabase(connection);

        string protectedValue;
        using (var instanceA = CreateInstance(connection))
        {
            var protector = instanceA.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Taslim.Api.Tests.CrossInstance");
            protectedValue = protector.Protect("csrf-and-auth-protected-payload");
        }

        using (var instanceB = CreateInstance(connection))
        {
            var protector = instanceB.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Taslim.Api.Tests.CrossInstance");
            Assert.Equal("csrf-and-auth-protected-payload", protector.Unprotect(protectedValue));
        }
    }

    [Fact]
    public void Different_application_names_do_not_share_protected_payloads()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        EnsureDatabase(connection);

        string protectedValue;
        using (var instanceA = CreateInstance(connection, "Taslim.Api"))
        {
            protectedValue = instanceA.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("ApplicationIsolation")
                .Protect("payload");
        }

        using var instanceB = CreateInstance(connection, "Different.Application");
        var protector = instanceB.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("ApplicationIsolation");
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(protectedValue));
    }

    private static void EnsureDatabase(SqliteConnection connection)
    {
        using var provider = CreateInstance(connection);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TaslimDbContext>().Database.EnsureCreated();
    }

    private static ServiceProvider CreateInstance(SqliteConnection connection, string applicationName = "Taslim.Api")
    {
        var services = new ServiceCollection();
        services.AddDbContext<TaslimDbContext>(options => options.UseSqlite(connection));
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToDbContext<TaslimDbContext>();
        return services.BuildServiceProvider();
    }
}
