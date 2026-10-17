using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:ApplyMigrations")) return;
        try
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<CompareShopDbContext>().Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            app.Services.GetRequiredService<MigrationState>().Failed = true;
            app.Logger.LogError(ex, "Database migration failed; readiness unavailable");
        }
    }
}
