using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Files;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class ProductImageApplicationTests
{
    [Fact]
    public async Task Product_creation_paths_create_the_initial_product_image_association()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new CompareShopDbContext(options);
        const string subject = "product-image-creator";
        var user = new User(subject, DateTime.UtcNow);
        var role = new AdminRole { Name = "product-image-creator" };
        var category = new Category { Slug = "laptops", Name = "Laptops", Level = 2 };
        var permissions = new[]
        {
            new AdminPermission { Key = AdminPermissions.ProductsCreate },
            new AdminPermission { Key = AdminPermissions.MatchingApprove }
        };
        var matching = new CompareShopAPI.Domain.Admin.ProductMatching("Matching laptop", "Brand", DateTime.UtcNow);
        db.Users.Add(user);
        db.AdminRoles.Add(role);
        db.AdminPermissions.AddRange(permissions);
        db.Categories.Add(category);
        db.ProductMatchings.Add(matching);
        await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminRolePermissions.AddRange(permissions.Select(permission => new AdminRolePermission
            { RoleId = role.Id, PermissionId = permission.Id }));
        await db.SaveChangesAsync();

        var service = new AdminService(new EfAdminData(db, TimeProvider.System));
        var standardProduct = await service.CreateProduct(subject,
            new ProductInput("standard-product", "Standard Product", null, null, category.Id, ImageUrl: "https://images.example.test/standard.jpg"), default);
        var matchingProduct = await service.CreateMatchingProduct(subject, matching.Id,
            new MatchingProductCreate("Matching Product", null, null, category.Id, "https://images.example.test/matching.jpg", "shop"), default);

        var standardImage = Assert.Single(await db.ProductImages.Where(image => image.ProductId == standardProduct.Id).ToListAsync());
        Assert.Equal("https://images.example.test/standard.jpg", standardImage.ExternalUrl);
        Assert.Equal(0, standardImage.SortOrder);
        var matchingImage = Assert.Single(await db.ProductImages.Where(image => image.ProductId == matchingProduct.Id).ToListAsync());
        Assert.Equal("https://images.example.test/matching.jpg", matchingImage.ExternalUrl);
        Assert.Equal(0, matchingImage.SortOrder);
    }

    [Fact]
    public async Task Derivative_failure_keeps_valid_source_association_and_reports_per_size()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new CompareShopDbContext(options);
        var subject = "product-image-operator";
        var user = new User(subject, DateTime.UtcNow);
        var role = new AdminRole { Name = "product-image-operator" };
        var permission = new AdminPermission { Key = AdminPermissions.ProductsUpdate };
        var category = new Category { Slug = "category", Name = "Category" };
        var sourceId = Guid.NewGuid();
        var product = new Product { Slug = "phone", Name = "Phone", Category = category };
        db.Users.Add(user);
        db.AdminRoles.Add(role);
        db.AdminPermissions.Add(permission);
        db.Products.Add(product);
        db.Files.Add(new FileAsset(sourceId, "source.jpg", "image/jpeg", 20, "source", subject, DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var derivatives = new FailedDerivatives();
        var service = new AdminService(new EfAdminData(db, TimeProvider.System), fileService: derivatives);

        var result = await service.ReplaceProductImages(subject, product.Id,
            [new ProductImageInput(null, 0, "Front", sourceId, null)], default);

        var image = Assert.Single(result!);
        Assert.Equal("failed", image.Derivative600Status);
        Assert.Equal("failed", image.Derivative1000Status);
        Assert.Equal($"/files/{sourceId:D}", image.Url600);
        Assert.Equal($"/files/{sourceId:D}", image.Url1000);
        Assert.Equal($"/files/{sourceId:D}", (await db.Products.SingleAsync(x => x.Id == product.Id)).ImageUrl);
        Assert.Single(await db.ProductImages.Where(x => x.ProductId == product.Id).ToListAsync());
        Assert.Contains(await db.AdminAuditLogs.ToListAsync(), entry => entry.ResourceType == "product_images" && entry.Action == "generate_derivatives");
        Assert.Equal(1, derivatives.CallCount);

        await service.RetryProductImageDerivatives(subject, product.Id, image.Id, default);
        Assert.Equal(2, derivatives.CallCount);
    }

    [Fact]
    public async Task Automatic_derivatives_and_manual_retry_use_supported_quality_and_retry_succeeds()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new CompareShopDbContext(options);
        const string subject = "product-image-operator";
        var user = new User(subject, DateTime.UtcNow);
        var role = new AdminRole { Name = "product-image-operator" };
        var permission = new AdminPermission { Key = AdminPermissions.ProductsUpdate };
        var category = new Category { Slug = "category", Name = "Category" };
        var sourceId = Guid.NewGuid();
        var product = new Product { Slug = "phone", Name = "Phone", Category = category };
        db.Users.Add(user);
        db.AdminRoles.Add(role);
        db.AdminPermissions.Add(permission);
        db.Products.Add(product);
        db.Files.Add(new FileAsset(sourceId, "source.jpg", "image/jpeg", 20, "source", subject, DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var derivatives = new RetryableDerivatives(db);
        var service = new AdminService(new EfAdminData(db, TimeProvider.System), fileService: derivatives);

        var initial = Assert.Single((await service.ReplaceProductImages(subject, product.Id,
            [new ProductImageInput(null, 0, "Front", sourceId, null)], default))!);
        Assert.Equal("failed", initial.Derivative600Status);
        Assert.Equal("failed", initial.Derivative1000Status);

        var retried = await service.RetryProductImageDerivatives(subject, product.Id, initial.Id, default);

        Assert.Equal("ready", retried!.Derivative600Status);
        Assert.Equal("ready", retried.Derivative1000Status);
        Assert.NotEqual($"/files/{sourceId:D}", retried.Url600);
        Assert.NotEqual($"/files/{sourceId:D}", retried.Url1000);
        Assert.Equal(2, derivatives.Requests.Count);
        Assert.All(derivatives.Requests, variants =>
        {
            Assert.Collection(variants,
                variant => { Assert.Equal(600, variant.Width); Assert.Equal(600, variant.Height); Assert.Equal(80, variant.Quality); },
                variant => { Assert.Equal(1000, variant.Width); Assert.Equal(1000, variant.Height); Assert.Equal(80, variant.Quality); });
        });
    }

    [Fact]
    public async Task Replace_images_with_derivatives_disabled_persists_file_and_external_images_without_generating()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new CompareShopDbContext(options);
        const string subject = "product-image-operator";
        var user = new User(subject, DateTime.UtcNow);
        var role = new AdminRole { Name = "product-image-operator" };
        var permission = new AdminPermission { Key = AdminPermissions.ProductsUpdate };
        var category = new Category { Slug = "category", Name = "Category" };
        var sourceId = Guid.NewGuid();
        var product = new Product { Slug = "phone", Name = "Phone", Category = category };
        db.Users.Add(user);
        db.AdminRoles.Add(role);
        db.AdminPermissions.Add(permission);
        db.Products.Add(product);
        db.Files.Add(new FileAsset(sourceId, "source.jpg", "image/jpeg", 20, "source", subject, DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var derivatives = new FailedDerivatives();
        var service = new AdminService(new EfAdminData(db, TimeProvider.System), fileService: derivatives);

        var result = await service.ReplaceProductImages(subject, product.Id,
            [
                new ProductImageInput(null, 0, "Front", sourceId, null),
                new ProductImageInput(null, 1, "External", null, "https://images.example.test/back.jpg")
            ], default, createDerivatives: false);

        Assert.Equal(0, derivatives.CallCount);
        Assert.Collection(result!,
            image => { Assert.Equal("Front", image.AltText); Assert.Equal(0, image.SortOrder); Assert.Equal("pending", image.Derivative600Status); },
            image => { Assert.Equal("External", image.AltText); Assert.Equal(1, image.SortOrder); Assert.Null(image.FileId); });
        Assert.Equal(2, await db.ProductImages.CountAsync(image => image.ProductId == product.Id));
        Assert.Equal($"/files/{sourceId:D}", (await db.Products.SingleAsync(x => x.Id == product.Id)).ImageUrl);
    }

    private sealed class FailedDerivatives : IFileDerivatives
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<DerivativeVariantOutcome>>(request.Variants.Select((_, index) =>
                new DerivativeVariantOutcome(index, null, null, "storage_failed", [])).ToArray());
        }
    }

    private sealed class RetryableDerivatives(CompareShopDbContext db) : IFileDerivatives
    {
        public List<IReadOnlyList<DerivativeVariant>> Requests { get; } = [];

        public async Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid sourceId, DerivativeRequest request, CancellationToken ct)
        {
            Requests.Add(request.Variants);
            if (Requests.Count == 1)
                return request.Variants.Select((_, index) => new DerivativeVariantOutcome(index, null, null, "transient_failure", [])).ToArray();

            var now = DateTime.UtcNow;
            var outcomes = new List<DerivativeVariantOutcome>();
            for (var index = 0; index < request.Variants.Count; index++)
            {
                var variant = request.Variants[index];
                var id = Guid.NewGuid();
                var preset = variant.Width == 600 ? "home-600x600" : "product-detail-1000x1000";
                var name = $"product-{variant.Width}x{variant.Height}.webp";
                var asset = new FileAsset(id, name, "image/webp", 50, $"derivatives/{id:N}", subject!, now,
                    variant.Width, variant.Height, sourceId, preset);
                db.Files.Add(asset);
                outcomes.Add(new DerivativeVariantOutcome(index,
                    new FileMetadata(id, name, "image/webp", 50, variant.Width, variant.Height, subject!, now,
                        new DerivedFromMetadata(sourceId, "source.jpg"), preset,
                        new DerivativeSpec(1, variant.Width, variant.Height, variant.Fit, variant.FocalX, variant.FocalY,
                            variant.Background, variant.Format, variant.Quality)),
                    $"/files/{id:D}", null, []));
            }
            await db.SaveChangesAsync(ct);
            return outcomes;
        }
    }
}
