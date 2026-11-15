using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class ProductImageAuthorizationTests
{
    [Fact]
    public async Task Image_routes_follow_products_read_and_products_update_permissions()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new CompareShopDbContext(options);
        var reader = new User("reader", DateTime.UtcNow);
        var updater = new User("updater", DateTime.UtcNow);
        var readRole = new AdminRole { Name = "product-image-reader" };
        var updateRole = new AdminRole { Name = "product-image-updater" };
        var read = new AdminPermission { Key = AdminPermissions.ProductsRead };
        var update = new AdminPermission { Key = AdminPermissions.ProductsUpdate };
        db.Users.AddRange(reader, updater);
        db.AdminRoles.AddRange(readRole, updateRole);
        db.AdminPermissions.AddRange(read, update);
        await db.SaveChangesAsync();
        db.AdminRolePermissions.AddRange(
            new AdminRolePermission { RoleId = readRole.Id, PermissionId = read.Id },
            new AdminRolePermission { RoleId = updateRole.Id, PermissionId = update.Id });
        db.AdminUserRoles.AddRange(
            new AdminUserRole { UserId = reader.Id, RoleId = readRole.Id },
            new AdminUserRole { UserId = updater.Id, RoleId = updateRole.Id });
        await db.SaveChangesAsync();
        var service = new AdminService(new EfAdminData(db, TimeProvider.System));

        Assert.Null(await service.ProductImages("reader", int.MaxValue, default));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.ReplaceProductImages("reader", int.MaxValue, [], default));
        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.ProductImages("updater", int.MaxValue, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReplaceProductImages("updater", int.MaxValue, [], default));
    }
}
