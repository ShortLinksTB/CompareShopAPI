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
        Assert.Equal("storage_failed", image.Derivative600Error);
        Assert.Equal("storage_failed", image.Derivative1000Error);
        Assert.Equal($"/files/{sourceId:D}", image.Url600);
        Assert.Equal($"/files/{sourceId:D}", image.Url1000);
        Assert.Equal($"/files/{sourceId:D}", (await db.Products.SingleAsync(x => x.Id == product.Id)).ImageUrl);
        Assert.Single(await db.ProductImages.Where(x => x.ProductId == product.Id).ToListAsync());
        Assert.Contains(await db.AdminAuditLogs.ToListAsync(), entry => entry.ResourceType == "product_images" && entry.Action == "generate_derivatives");
        Assert.Equal(1, derivatives.CallCount);

        var retryResult = await service.RetryProductImageDerivatives(subject, product.Id, image.Id, default);
        Assert.Equal(2, derivatives.CallCount);
        Assert.Equal("failed", retryResult!.Derivative600Status);
        Assert.Equal("storage_failed", retryResult.Derivative600Error);
        Assert.Equal("failed", retryResult.Derivative1000Status);
        Assert.Equal("storage_failed", retryResult.Derivative1000Error);
    }

    [Fact]
    public async Task Retry_persists_and_returns_source_pixel_limit_failure_for_each_size()
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
        var product = new Product { Slug = "oversized-source-retry", Name = "Oversized Source Retry", Category = category };
        db.Users.Add(user);
        db.AdminRoles.Add(role);
        db.AdminPermissions.Add(permission);
        db.Products.Add(product);
        db.Files.Add(new FileAsset(sourceId, "qc-sl2370-25mp.png", "image/png", 20, "source", subject, DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var derivatives = new FailedDerivatives("source_pixel_limit_exceeded");
        var service = new AdminService(new EfAdminData(db, TimeProvider.System), fileService: derivatives);
        var image = Assert.Single((await service.ReplaceProductImages(subject, product.Id,
            [new ProductImageInput(null, 0, "Front", sourceId, null)], default))!);

        var retried = await service.RetryProductImageDerivatives(subject, product.Id, image.Id, default);
        var persisted = await db.ProductImages.SingleAsync(x => x.Id == image.Id);

        Assert.Equal("failed", retried!.Derivative600Status);
        Assert.Equal("source_pixel_limit_exceeded", retried.Derivative600Error);
        Assert.Equal("failed", retried.Derivative1000Status);
        Assert.Equal("source_pixel_limit_exceeded", retried.Derivative1000Error);
        Assert.Equal("failed", persisted.Card600Status);
        Assert.Equal("source_pixel_limit_exceeded", persisted.Card600Error);
        Assert.Equal("failed", persisted.Detail1000Status);
        Assert.Equal("source_pixel_limit_exceeded", persisted.Detail1000Error);
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
        Assert.Null(retried.Derivative600Error);
        Assert.Null(retried.Derivative1000Error);
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
    public async Task Retry_persists_and_returns_mixed_derivative_outcomes()
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
        var product = new Product { Slug = "mixed-retry", Name = "Mixed Retry", Category = category };
        db.Users.Add(user);
        db.AdminRoles.Add(role);
        db.AdminPermissions.Add(permission);
        db.Products.Add(product);
        db.Files.Add(new FileAsset(sourceId, "source.jpg", "image/jpeg", 20, "source", subject, DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var derivatives = new MixedRetryDerivatives(db);
        var service = new AdminService(new EfAdminData(db, TimeProvider.System), fileService: derivatives);
        var initial = Assert.Single((await service.ReplaceProductImages(subject, product.Id,
            [new ProductImageInput(null, 0, "Front", sourceId, null)], default))!);

        var retried = await service.RetryProductImageDerivatives(subject, product.Id, initial.Id, default);
        var persisted = await db.ProductImages.SingleAsync(image => image.Id == initial.Id);

        Assert.Equal("ready", persisted.Card600Status);
        Assert.Equal(derivatives.SuccessfulDerivativeId, persisted.Card600FileId);
        Assert.Null(persisted.Card600Error);
        Assert.Equal("failed", persisted.Detail1000Status);
        Assert.Null(persisted.Detail1000FileId);
        Assert.Equal("transient_failure", persisted.Detail1000Error);
        Assert.Equal("ready", retried!.Derivative600Status);
        Assert.Equal($"/files/{derivatives.SuccessfulDerivativeId:D}", retried.Url600);
        Assert.Equal("failed", retried.Derivative1000Status);
        Assert.Equal("transient_failure", retried.Derivative1000Error);
        Assert.Equal($"/files/{sourceId:D}", retried.Url1000);
    }

    [Theory]
    [InlineData("forbidden", "permission_denied")]
    [InlineData("missing", "source_not_found")]
    [InlineData("unexpected", "derivative_service_failed")]
    public async Task Retry_persists_concrete_failure_code_when_derivative_service_throws(string failure, string expectedError)
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
        var product = new Product { Slug = "retry-error", Name = "Retry Error", Category = category };
        db.Users.Add(user);
        db.AdminRoles.Add(role);
        db.AdminPermissions.Add(permission);
        db.Products.Add(product);
        db.Files.Add(new FileAsset(sourceId, "source.jpg", "image/jpeg", 20, "source", subject, DateTime.UtcNow));
        await db.SaveChangesAsync();
        db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var derivatives = new ThrowingDerivatives(failure);
        var service = new AdminService(new EfAdminData(db, TimeProvider.System), fileService: derivatives);
        var image = Assert.Single((await service.ReplaceProductImages(subject, product.Id,
            [new ProductImageInput(null, 0, "Front", sourceId, null)], default))!);

        var retried = await service.RetryProductImageDerivatives(subject, product.Id, image.Id, default);

        Assert.Equal("failed", retried!.Derivative600Status);
        Assert.Equal(expectedError, retried.Derivative600Error);
        Assert.Equal("failed", retried.Derivative1000Status);
        Assert.Equal(expectedError, retried.Derivative1000Error);
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
        private readonly string error;

        public FailedDerivatives(string error = "storage_failed") => this.error = error;

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<DerivativeVariantOutcome>>(request.Variants.Select((_, index) =>
                new DerivativeVariantOutcome(index, null, null, error, [])).ToArray());
        }
    }

    private sealed class ThrowingDerivatives(string failure) : IFileDerivatives
    {
        public Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<DerivativeVariantOutcome>>(failure switch
            {
                "forbidden" => new AdminForbiddenException(),
                "missing" => new KeyNotFoundException(),
                _ => new InvalidOperationException("service unavailable")
            });
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

    private sealed class MixedRetryDerivatives(CompareShopDbContext db) : IFileDerivatives
    {
        private int calls;
        public Guid SuccessfulDerivativeId { get; } = Guid.NewGuid();

        public async Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid sourceId, DerivativeRequest request, CancellationToken ct)
        {
            if (++calls == 1)
                return request.Variants.Select((_, index) => new DerivativeVariantOutcome(index, null, null, "initial_failure", [])).ToArray();

            var cardVariant = request.Variants[0];
            var now = DateTime.UtcNow;
            var asset = new FileAsset(SuccessfulDerivativeId, "product-600x600.webp", "image/webp", 50,
                $"derivatives/{SuccessfulDerivativeId:N}", subject!, now, 600, 600, sourceId, "home-600x600");
            db.Files.Add(asset);
            await db.SaveChangesAsync(ct);
            var card = new FileMetadata(SuccessfulDerivativeId, asset.OriginalFileName, asset.ContentType, asset.SizeBytes,
                600, 600, subject!, now, new DerivedFromMetadata(sourceId, "source.jpg"), "home-600x600",
                new DerivativeSpec(1, cardVariant.Width, cardVariant.Height, cardVariant.Fit, cardVariant.FocalX,
                    cardVariant.FocalY, cardVariant.Background, cardVariant.Format, cardVariant.Quality));
            return
            [
                new DerivativeVariantOutcome(0, card, $"/files/{SuccessfulDerivativeId:D}", null, []),
                new DerivativeVariantOutcome(1, null, null, "transient_failure", [])
            ];
        }
    }
}
