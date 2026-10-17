using Testcontainers.MsSql;
using Xunit;

namespace CompareShopAPI.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerTestCollection : ICollectionFixture<SqlServerTestFixture>
{
    public const string Name = "Isolated SQL Server";
}

public sealed class SqlServerTestFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        try
        {
            await _container.StopAsync();
        }
        finally
        {
            await _container.DisposeAsync();
        }
    }
}
