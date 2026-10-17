using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Api.Catalog;

public static class CatalogSeedCommand
{
    public static bool IsRequested(string[] args) => args.Length == 1 && args[0] == "seed-catalog";

    public static async Task<bool> TryRunAsync(
        string[] args,
        IHostEnvironment environment,
        IConfiguration configuration,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!IsRequested(args)) return false;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Catalog seeding is only allowed in the Development environment.");
        if (!configuration.GetValue<bool>("Database:AllowCatalogSeed"))
            throw new InvalidOperationException("Catalog seeding is disabled. Set Database:AllowCatalogSeed=true explicitly for the one-off dev seed command.");

        var connectionString = configuration.GetConnectionString("CompareShopDb");
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !string.Equals(new SqlConnectionStringBuilder(connectionString).InitialCatalog, "CompareShop", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Catalog seeding requires a connection string targeting the CompareShop database.");

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
        if (!await db.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("Cannot connect to the CompareShop database; catalog seed was not applied.");

        await CatalogSeed.SeedAsync(db, cancellationToken);
        logger.LogInformation("Catalog seed completed for the explicitly selected CompareShop database.");
        return true;
    }
}
