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
        var service = new AdminService(new EfAdminData(db, TimeProvider.System), fileService: new FailedDerivatives());

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
    }

    private sealed class FailedDerivatives : IFileDerivatives
    {
        public Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DerivativeVariantOutcome>>(request.Variants.Select((_, index) =>
                new DerivativeVariantOutcome(index, null, null, "storage_failed", [])).ToArray());
    }
}
