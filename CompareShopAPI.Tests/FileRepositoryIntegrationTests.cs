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
                var sourceId = Guid.NewGuid();
                await repository.Add(new FileAsset(sourceId, "photo.png", "image/png", 3, "photo", "operator", DateTime.UtcNow), default);
                var derivative = new FileAsset(Guid.NewGuid(), "photo-32x32.png", "image/png", 10, "derived", "operator", DateTime.UtcNow,
                    32, 32, sourceId, "custom", "{\"version\":1,\"width\":32,\"height\":32,\"fit\":\"cover\",\"focalX\":0.5,\"focalY\":0.5,\"background\":\"transparent\",\"format\":\"png\",\"quality\":80}");
                await repository.ReserveDerivativeAndAdd(derivative, derivative.OriginalFileName, default);
                await repository.AuditDerivative("operator", sourceId, 1, "{}", null, "invalid_variant", default);
                await repository.Add(new FileAsset(Guid.NewGuid(), "manual.pdf", "application/pdf", 5, "manual", "operator", DateTime.UtcNow), default);
            }

            await using (var db = new CompareShopDbContext(options))
            {
                var repository = new EfFileRepository(db);
                var photos = (await repository.List(1, 20, "photo", "images", default)).Items;
                Assert.Equal(2, photos.Count);
                var derivative = Assert.Single(photos, file => file.DerivedFrom is not null);
                Assert.Equal("photo.png", derivative.DerivedFrom!.OriginalFileName);
                Assert.Equal("custom", derivative.DerivedPreset);
                Assert.Equal(32, derivative.DerivativeSpec!.Width);
                Assert.Single((await repository.List(1, 20, null, "other", default)).Items);
                Assert.Equal(3, (await repository.List(1, 1, null, null, default)).TotalItems);
                var sourceId = await db.Files.Where(file => file.OriginalFileName == "photo.png").Select(file => file.Id).SingleAsync();
                var first = ReserveName(connection, sourceId, "race.png");
                var second = ReserveName(connection, sourceId, "RACE.PNG");
                var reserved = await Task.WhenAll(first, second);
                Assert.Single(reserved, result => result);
                var photoId = await db.Files.Where(file => file.OriginalFileName == "photo.png").Select(file => file.Id).SingleAsync();
                var photo = await repository.Get(photoId, default);
                Assert.NotNull(photo);
                Assert.Null(photo!.DerivedFromFileId);
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

    private static async Task<bool> ReserveName(string connection, Guid sourceId, string name)
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        await using var db = new CompareShopDbContext(options);
        var file = new FileAsset(Guid.NewGuid(), name, "image/png", 10, Guid.NewGuid().ToString("N"), "operator", DateTime.UtcNow,
            16, 16, sourceId, "custom", "{}");
        try
        {
            await new EfFileRepository(db).ReserveDerivativeAndAdd(file, name, default);
            return true;
        }
        catch (DerivativeNameCollisionException) { return false; }
    }
}
