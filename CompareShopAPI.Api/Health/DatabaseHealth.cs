using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using CompareShopAPI.Api.Catalog;
namespace CompareShopAPI.Api.Health;
public sealed class MigrationState { public bool Failed { get; set; } }
public sealed class DatabaseHealth(IServiceProvider services, MigrationState migration, IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (migration.Failed || string.IsNullOrWhiteSpace(config.GetConnectionString("CompareShopDb")))
            return HealthCheckResult.Unhealthy("Database unavailable");
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable");
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Database unavailable"); }
    }
}
public static class DatabaseStartup
{
    private const string CountrySchemaStage = "20261118170350_AddCountryAndNullableCatalogCountryIds";
    private const string LegacyCountrySeedStage = "20261118170355_AssignLegacyCatalogSeedRowsToVN";

    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:ApplyMigrations")) return;
        try
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
            var applied = await db.Database.GetAppliedMigrationsAsync();
            if (!applied.Contains(CountrySchemaStage, StringComparer.Ordinal))
            {
                var pending = await db.Database.GetPendingMigrationsAsync();
                if (!pending.Contains(CountrySchemaStage, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Required staged migration '{CountrySchemaStage}' is not available.");
                await db.Database.MigrateAsync(CountrySchemaStage);
            }
            await CountrySeed.SeedInitialAsync(db, typeof(CatalogSeedCommand).Assembly);
            await db.Database.MigrateAsync(LegacyCountrySeedStage);
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            app.Services.GetRequiredService<MigrationState>().Failed = true;
            app.Logger.LogError(ex, "Database migration failed; readiness unavailable");
        }
    }
}
