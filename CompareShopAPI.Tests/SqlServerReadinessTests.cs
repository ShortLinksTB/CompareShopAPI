using System.Net;
using CompareShopAPI.Api.Health;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class SqlServerReadinessTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task SqlServer_readiness_uses_real_database()
    {
        await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = sqlServer.ConnectionString });
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        app.Services.GetRequiredService<MigrationState>().Failed = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
