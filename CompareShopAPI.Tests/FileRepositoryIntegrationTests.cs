using CompareShopAPI.Application.Files;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Infrastructure.Files;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Application.Articles;
using CompareShopAPI.Infrastructure.Articles;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Infrastructure.Admin;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class FileRepositoryIntegrationTests(SqlServerTestFixture sqlServer)
{
    [Fact]
    public async Task Product_reference_writer_and_delete_are_serialized_by_file_row_lock()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopFileRace_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        try
        {
            int productId;
            Guid fileId = Guid.NewGuid();
            await using (var db = new CompareShopDbContext(options))
            {
                await MigrationTestHelpers.MigrateToLatestAsync(db);
                var parent = new Category { Slug = "race-parent", Name = "Race parent", Level = 1, CountryId = CountryTestCompatibility.VnCountryId };
                var child = new Category { Slug = "race-child", Name = "Race child", Level = 2, ParentCategory = parent, CountryId = CountryTestCompatibility.VnCountryId };
                var product = new Product { Slug = "race-product", Name = "Race product", Category = child };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                productId = product.Id;
                await new EfFileRepository(db).Add(new FileAsset(fileId, "race.png", "image/png", 1, "race", "operator", DateTime.UtcNow), default);
            }

            using var barrier = new Barrier(2);
            var writer = Task.Run(async () =>
            {
                await using var db = new CompareShopDbContext(options);
                barrier.SignalAndWait();
                try
                {
                    await new EfAdminData(db, TimeProvider.System).ReplaceProductImages(productId,
                        [new ProductImageInput(null, 0, null, fileId, null)], DateTime.UtcNow, default);
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            });
            var delete = Task.Run(async () =>
            {
                await using var db = new CompareShopDbContext(options);
                barrier.SignalAndWait();
                return await new EfFileRepository(db).BeginDelete(fileId, default);
            });
            var writerSucceeded = await writer;
            var deletion = await delete;

            await using var verify = new CompareShopDbContext(options);
            var state = await verify.Files.Where(x => x.Id == fileId).Select(x => x.LifecycleState).SingleAsync();
            var referenceExists = await verify.ProductImages.AnyAsync(x => x.FileId == fileId);
            if (writerSucceeded)
            {
                Assert.True(referenceExists);
                Assert.Equal(FileLifecycleState.Active, state);
                Assert.Equal("in_use", deletion.State);
            }
            else
            {
                Assert.False(referenceExists);
                Assert.Equal(FileLifecycleState.Deleting, state);
                Assert.Equal("deleting", deletion.State);
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Find_derivative_ignores_deleted_rows_so_they_can_be_regenerated()
    {
        var connection = new SqlConnectionStringBuilder(sqlServer.ConnectionString) { InitialCatalog = $"CompareShopDeletedDerivative_{Guid.NewGuid():N}" }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var sourceId = Guid.NewGuid();
        const string spec = "{\"Version\":1,\"Width\":32,\"Height\":32,\"Fit\":\"cover\",\"FocalX\":0.5,\"FocalY\":0.5,\"Background\":\"transparent\",\"Format\":\"png\",\"Quality\":80}";
        try
        {
            await using var db = new CompareShopDbContext(options);
            await MigrationTestHelpers.MigrateToLatestAsync(db);
            var repository = new EfFileRepository(db);
            await repository.Add(new FileAsset(sourceId, "source.png", "image/png", 3, "source", "operator", DateTime.UtcNow), default);
            var deleted = new FileAsset(Guid.NewGuid(), "source-32x32.png", "image/png", 3, "deleted-derivative", "operator",
                DateTime.UtcNow, 32, 32, sourceId, "custom", spec);
            deleted.BeginDeletion();
            deleted.CompleteDeletion();
            await db.Files.AddAsync(deleted);
            await db.SaveChangesAsync();

            Assert.Null(await repository.FindDerivative(sourceId, "custom", spec, default));

            var replacement = new FileAsset(Guid.NewGuid(), "source-32x32-2.png", "image/png", 3, "replacement", "operator",
                DateTime.UtcNow, 32, 32, sourceId, "custom", spec);
            await repository.ReserveDerivativeAndAdd(replacement, replacement.OriginalFileName, default);
            Assert.Equal(replacement.Id, (await repository.FindDerivative(sourceId, "custom", spec, default))!.Id);
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

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
                await MigrationTestHelpers.MigrateToLatestAsync(db);
                Assert.False(db.Database.HasPendingModelChanges());
                var readId = await db.AdminPermissions.Where(permission => permission.Key == "files.read").Select(permission => permission.Id).SingleAsync();
                var writeId = await db.AdminPermissions.Where(permission => permission.Key == "files.write").Select(permission => permission.Id).SingleAsync();
                Assert.Equal(new[] { 1, 2, 4 }, await db.AdminRolePermissions.Where(grant => grant.PermissionId == readId).Select(grant => grant.RoleId).Order().ToArrayAsync());
                Assert.Equal(new[] { 1, 2 }, await db.AdminRolePermissions.Where(grant => grant.PermissionId == writeId).Select(grant => grant.RoleId).Order().ToArrayAsync());
                var repository = new EfFileRepository(db);
                var sourceId = Guid.NewGuid();
                await repository.Add(new FileAsset(sourceId, "photo.png", "image/png", 3, "photo", "operator", DateTime.UtcNow), default);
                var topCategory = new Category { Slug = "top-files", Name = "Top", Level = 1, CountryId = CountryTestCompatibility.VnCountryId };
                var subCategory = new Category { Slug = "sub-files", Name = "Sub", Level = 2, ParentCategory = topCategory, CountryId = CountryTestCompatibility.VnCountryId };
                var product = new Product { Slug = "file-usage-product", Name = "File usage product", Category = subCategory };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                db.ProductImages.Add(ProductImage.CreateFile(product.Id, 0, sourceId, null, DateTime.UtcNow));
                await db.SaveChangesAsync();
                var blog = new EfArticleRepository(db);
                await blog.BlogCreate(new BlogArticleInput("File cover", "file-cover", "shopping-tips", "Excerpt", "Body", sourceId.ToString("D"), []),
                    "operator", "Operator", DateTime.UtcNow, default);
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
                var photoRow = Assert.Single(photos, file => file.OriginalFileName == "photo.png");
                Assert.Equal(new[] { "blog", "derivative", "product" }, photoRow.UsedBy!.Select(x => x.Type).Order().ToArray());
                Assert.Contains(photoRow.UsedBy!, usage => usage.Label == "Sản phẩm #1: File usage product");
                Assert.Contains(photoRow.UsedBy!, usage => usage.Label == "Blog: File cover");
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
                var deletion = await repository.BeginDelete(photoId, default);
                Assert.Equal("in_use", deletion.State);
                Assert.Equal(4, deletion.UsedBy.Count); // product, blog, and both derivative assets
                var tombstonedId = Guid.NewGuid();
                await repository.Add(new FileAsset(tombstonedId, "pending.png", "image/png", 2, "pending", "operator", DateTime.UtcNow), default);
                Assert.Equal("deleting", (await repository.BeginDelete(tombstonedId, default)).State);
                var adminData = new EfAdminData(db, TimeProvider.System);
                var productId = await db.Products.Where(x => x.Slug == "file-usage-product").Select(x => x.Id).SingleAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => adminData.ReplaceProductImages(productId,
                    [new ProductImageInput(null, 0, "Pending", tombstonedId, null)], DateTime.UtcNow, default));
                Assert.Equal(FileLifecycleState.Deleting, await db.Files.Where(x => x.Id == tombstonedId).Select(x => x.LifecycleState).SingleAsync());
                var id = await db.Files.Where(file => file.OriginalFileName == "manual.pdf").Select(file => file.Id).SingleAsync();
                Assert.Equal("deleting", (await repository.BeginDelete(id, default)).State);
                Assert.True(await repository.FinalizeDelete(id, default));
                Assert.Equal("deleted", (await repository.BeginDelete(id, default)).State);
                Assert.Null(await repository.Get(id, default));
                Assert.Equal(3, (await repository.List(1, 20, null, null, default)).TotalItems);
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
