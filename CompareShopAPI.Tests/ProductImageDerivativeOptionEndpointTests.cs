using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Files;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Files;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class ProductImageDerivativeOptionEndpointTests
{
    [Fact]
    public async Task Put_binds_camel_case_option_and_defaults_to_generating_derivatives()
    {
        const string subject = "product-image-option-admin";
        var database = Guid.NewGuid().ToString("N");
        var derivatives = new CountingDerivatives();
        await using var app = new ApiFactory(signing: true, configureServices: services =>
        {
            services.RemoveAll<DbContextOptions<CompareShopDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CompareShopDbContext>>();
            services.RemoveAll<CompareShopDbContext>();
            services.AddDbContext<CompareShopDbContext>(options => options.UseInMemoryDatabase(database)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.RemoveAll<IFileDerivatives>();
            services.AddSingleton<IFileDerivatives>(derivatives);
        });
        int omittedProductId;
        int enabledProductId;
        int disabledProductId;
        Guid omittedFileId;
        Guid enabledFileId;
        Guid disabledFileId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
            var user = new User(subject, DateTime.UtcNow);
            var role = new AdminRole { Name = "product-image-option-admin" };
            var permission = new AdminPermission { Key = AdminPermissions.ProductsUpdate };
            var category = new Category { Slug = "image-options", Name = "Image options" };
            var products = new[]
            {
                new Product { Slug = "omitted-option", Name = "Omitted option", Category = category },
                new Product { Slug = "enabled-option", Name = "Enabled option", Category = category },
                new Product { Slug = "disabled-option", Name = "Disabled option", Category = category }
            };
            var files = Enumerable.Range(0, 3).Select(index => new FileAsset(Guid.NewGuid(), $"source-{index}.jpg", "image/jpeg", 20, "source", subject, DateTime.UtcNow)).ToArray();
            db.Users.Add(user);
            db.AdminRoles.Add(role);
            db.AdminPermissions.Add(permission);
            db.Categories.Add(category);
            db.Products.AddRange(products);
            db.Files.AddRange(files);
            await db.SaveChangesAsync();
            db.AdminUserRoles.Add(new AdminUserRole { UserId = user.Id, RoleId = role.Id });
            db.AdminRolePermissions.Add(new AdminRolePermission { RoleId = role.Id, PermissionId = permission.Id });
            await db.SaveChangesAsync();
            omittedProductId = products[0].Id;
            enabledProductId = products[1].Id;
            disabledProductId = products[2].Id;
            (omittedFileId, enabledFileId, disabledFileId) = (files[0].Id, files[1].Id, files[2].Id);
        }

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: subject));
        var omitted = await client.PutAsJsonAsync($"/admin/products/{omittedProductId}/images",
            new[] { new ProductImageInput(null, 0, "Omitted", omittedFileId, null) });
        var enabled = await client.PutAsJsonAsync($"/admin/products/{enabledProductId}/images?createDerivatives=true",
            new[] { new ProductImageInput(null, 0, "Enabled", enabledFileId, null) });
        var disabled = await client.PutAsJsonAsync($"/admin/products/{disabledProductId}/images?createDerivatives=false",
            new[] { new ProductImageInput(null, 0, "Disabled", disabledFileId, null) });

        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(2, derivatives.CallCount);
        Assert.Equal(2, derivatives.Requests.Count);
        Assert.All(derivatives.Requests, variants => Assert.All(variants, variant => Assert.Equal(80, variant.Quality)));
        await using var verifyScope = app.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
        var disabledImage = Assert.Single(await verify.ProductImages.Where(image => image.ProductId == disabledProductId).ToListAsync());
        Assert.Equal(disabledFileId, disabledImage.FileId);
        Assert.Equal("Disabled", disabledImage.AltText);
        Assert.Equal(0, disabledImage.SortOrder);
    }

    private sealed class CountingDerivatives : IFileDerivatives
    {
        public int CallCount { get; private set; }
        public List<IReadOnlyList<DerivativeVariant>> Requests { get; } = [];

        public Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct)
        {
            CallCount++;
            Requests.Add(request.Variants);
            return Task.FromResult<IReadOnlyList<DerivativeVariantOutcome>>([]);
        }
    }
}
