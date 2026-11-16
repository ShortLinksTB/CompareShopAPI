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
            services.AddSingleton<IFileDerivatives>(provider =>
            {
                derivatives.Configure(provider.GetRequiredService<DbContextOptions<CompareShopDbContext>>());
                return derivatives;
            });
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
        await using var verifyScope = app.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
        foreach (var productId in new[] { omittedProductId, enabledProductId })
        {
            var generated = Assert.Single(await verify.ProductImages.Where(image => image.ProductId == productId).ToListAsync());
            Assert.Equal("ready", generated.Card600Status);
            Assert.Equal("ready", generated.Detail1000Status);
            Assert.NotNull(generated.Card600FileId);
            Assert.NotNull(generated.Detail1000FileId);
        }
        var disabledImage = Assert.Single(await verify.ProductImages.Where(image => image.ProductId == disabledProductId).ToListAsync());
        Assert.Equal(disabledFileId, disabledImage.FileId);
        Assert.Equal("Disabled", disabledImage.AltText);
        Assert.Equal(0, disabledImage.SortOrder);
        Assert.Equal(new[] { (600, 600, 80), (1000, 1000, 80), (600, 600, 80), (1000, 1000, 80) },
            derivatives.Requests.SelectMany(request => request.Variants).Select(variant => (variant.Width, variant.Height, variant.Quality)));

        var retry = await client.PostAsync($"/admin/products/{disabledProductId}/images/{disabledImage.Id}/derivatives", null);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(3, derivatives.CallCount);
        await verify.Entry(disabledImage).ReloadAsync();
        Assert.Equal("ready", disabledImage.Card600Status);
        Assert.Equal("ready", disabledImage.Detail1000Status);
        Assert.Equal((600, 600, 80), derivatives.Requests[2].Variants[0] is { } card ? (card.Width, card.Height, card.Quality) : default);
        Assert.Equal((1000, 1000, 80), derivatives.Requests[2].Variants[1] is { } detail ? (detail.Width, detail.Height, detail.Quality) : default);
    }

    private sealed class CountingDerivatives : IFileDerivatives
    {
        private DbContextOptions<CompareShopDbContext> options = null!;
        public int CallCount { get; private set; }
        public List<DerivativeRequest> Requests { get; } = [];
        public void Configure(DbContextOptions<CompareShopDbContext> dbOptions) => options = dbOptions;

        public async Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct)
        {
            CallCount++;
            Requests.Add(request);
            await using var db = new CompareShopDbContext(options);
            var outcomes = request.Variants.Select((variant, index) =>
            {
                var fileId = Guid.NewGuid();
                var preset = variant.Width == 600 ? "home-600x600" : "product-detail-1000x1000";
                db.Files.Add(new FileAsset(fileId, variant.Name!, "image/webp", 100, fileId.ToString("N"), subject!, DateTime.UtcNow,
                    variant.Width, variant.Height, id, preset));
                return new DerivativeVariantOutcome(index,
                    new FileMetadata(fileId, variant.Name!, "image/webp", 100, variant.Width, variant.Height, subject!, DateTime.UtcNow,
                        new DerivedFromMetadata(id, "source.jpg"), preset), $"/files/{fileId:D}", null, []);
            }).ToArray();
            await db.SaveChangesAsync(ct);
            return outcomes;
        }
    }
}
