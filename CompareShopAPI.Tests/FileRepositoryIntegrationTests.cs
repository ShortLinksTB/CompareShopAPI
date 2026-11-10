using CompareShopAPI.Application.Files;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Infrastructure.Files;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class FileRepositoryIntegrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Migration_seeds_file_permissions_and_repository_filters_and_deletes_files()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = $"CompareShopFiles_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.False(db.Database.HasPendingModelChanges());
                var readId = await db.AdminPermissions.Where(permission => permission.Key == "files.read").Select(permission => permission.Id).SingleAsync();
                var writeId = await db.AdminPermissions.Where(permission => permission.Key == "files.write").Select(permission => permission.Id).SingleAsync();
                Assert.Equal(new[] { 1, 2, 4 }, await db.AdminRolePermissions.Where(grant => grant.PermissionId == readId).Select(grant => grant.RoleId).Order().ToArrayAsync());
                Assert.Equal(new[] { 1, 2 }, await db.AdminRolePermissions.Where(grant => grant.PermissionId == writeId).Select(grant => grant.RoleId).Order().ToArrayAsync());
                var repository = new EfFileRepository(db);
                await repository.Add(new FileAsset(Guid.NewGuid(), "photo.png", "image/png", 3, "photo", "operator", DateTime.UtcNow), default);
                await repository.Add(new FileAsset(Guid.NewGuid(), "manual.pdf", "application/pdf", 5, "manual", "operator", DateTime.UtcNow), default);
            }

            await using (var db = new CompareShopDbContext(options))
            {
                var repository = new EfFileRepository(db);
                Assert.Single((await repository.List(1, 20, "photo", "images", default)).Items);
                Assert.Single((await repository.List(1, 20, null, "other", default)).Items);
                Assert.Equal(2, (await repository.List(1, 1, null, null, default)).TotalItems);
                var photoId = await db.Files.Where(file => file.OriginalFileName == "photo.png").Select(file => file.Id).SingleAsync();
                Assert.NotNull(await repository.Get(photoId, default));
                var id = await db.Files.Where(file => file.OriginalFileName == "manual.pdf").Select(file => file.Id).SingleAsync();
                Assert.True(await repository.Delete(id, default));
                Assert.False(await repository.Delete(id, default));
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
}
