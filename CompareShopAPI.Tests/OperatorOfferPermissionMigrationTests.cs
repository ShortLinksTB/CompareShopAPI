using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class OperatorOfferPermissionMigrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Offers_create_is_granted_only_to_operator()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = $"CompareShopOperatorOfferPermission_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;

        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                await db.Database.MigrateAsync();

                var permissionId = await db.AdminPermissions
                    .Where(permission => permission.Key == "offers.create")
                    .Select(permission => permission.Id)
                    .SingleAsync();
                var grantedRoleIds = await db.AdminRolePermissions
                    .Where(grant => grant.PermissionId == permissionId)
                    .Select(grant => grant.RoleId)
                    .ToListAsync();

                Assert.Equal(new[] { 2 }, grantedRoleIds);

                await db.Database.MigrateAsync("20261105150000_AddOffersCreatePermission");
                Assert.False(await db.AdminRolePermissions.AnyAsync(grant => grant.PermissionId == permissionId));
                Assert.True(await db.AdminPermissions.AnyAsync(permission => permission.Id == permissionId));
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
