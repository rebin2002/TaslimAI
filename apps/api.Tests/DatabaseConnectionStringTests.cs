using Npgsql;
using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class DatabaseConnectionStringTests
{
    [Fact]
    public void Normalize_preserves_verify_full_and_root_certificate_from_uri()
    {
        const string rootCertificate = "/synthetic/certs/ca.pem";
        const string raw = "postgresql://synthetic-user:synthetic-password@db.example.test/taslim?sslmode=verify-full&sslrootcert=%2Fsynthetic%2Fcerts%2Fca.pem";

        var normalized = DatabaseConnectionString.Normalize(raw);
        var parsed = new NpgsqlConnectionStringBuilder(normalized);

        Assert.Equal(SslMode.VerifyFull, parsed.SslMode);
        Assert.Equal(rootCertificate, parsed.RootCertificate);
    }

    [Fact]
    public void Normalize_preserves_verify_ca_from_uri()
    {
        var normalized = DatabaseConnectionString.Normalize(
            "postgresql://synthetic-user:synthetic-password@db.example.test/taslim?sslmode=verify-ca");
        var parsed = new NpgsqlConnectionStringBuilder(normalized);

        Assert.Equal(SslMode.VerifyCA, parsed.SslMode);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("prefer")]
    public void Normalize_does_not_allow_uri_to_weaken_required_tls(string requestedMode)
    {
        var normalized = DatabaseConnectionString.Normalize(
            $"postgresql://synthetic-user:synthetic-password@db.example.test/taslim?sslmode={requestedMode}");
        var parsed = new NpgsqlConnectionStringBuilder(normalized);

        Assert.Equal(SslMode.Require, parsed.SslMode);
    }
}
