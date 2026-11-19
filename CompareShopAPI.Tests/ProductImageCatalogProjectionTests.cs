using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class ProductImageCatalogProjectionTests
{
    [Fact]
    public async Task Product_detail_projects_ordered_derivative_urls_and_source_fallback_without_internal_ids()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CompareShopDbContext(options);
        var vn = new Country("VN", "Vietnam");
        db.Countries.Add(vn);
        await db.SaveChangesAsync();
        var category = new Category { Slug = "phones", Name = "Phones", IsActive = true, CountryId = vn.Id, Country = vn };
        var shop = new PartnerShop { Slug = "shop", Name = "Shop", IsActive = true, CountryId = vn.Id, Country = vn };
        var firstFile = Guid.NewGuid();
        var firstCard = Guid.NewGuid();
        var firstDetail = Guid.NewGuid();
        var product = new Product
        {
            Slug = "phone", Name = "Phone", Category = category, IsActive = true,
            ImageUrl = $"/files/{firstCard:D}",
            Images =
            [
                new ProductImage { Id = Guid.NewGuid(), SortOrder = 0, FileId = firstFile, Card600FileId = firstCard, Detail1000FileId = firstDetail, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow },
                new ProductImage { Id = Guid.NewGuid(), SortOrder = 1, ExternalUrl = "https://images.example.test/second.jpg", CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow },
                new ProductImage { Id = Guid.NewGuid(), SortOrder = 2, FileId = Guid.NewGuid(), CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow }
            ]
        };
        product.Offers.Add(new ProductOffer { PartnerShop = shop, PriceVnd = 100, UpdatedAtUtc = DateTime.UtcNow, IsActive = true });
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var queries = new EfCatalogQueries(db, TimeProvider.System);
        var result = await queries.Product(product.Slug, default);
        var list = await queries.Products(new ProductFilter(1, 10, CountryId: CountryTestCompatibility.VnCountryId), default);

        Assert.NotNull(result);
        Assert.Equal($"/files/{firstCard:D}", result.ImageUrl);
        Assert.Equal($"/files/{firstCard:D}", Assert.Single(list.Items).ImageUrl);
        Assert.Collection(result.Images!,
            image => { Assert.Equal($"/files/{firstDetail:D}", image.Url1000); Assert.Equal($"/files/{firstCard:D}", image.Url600); },
            image => { Assert.Equal("https://images.example.test/second.jpg", image.Url1000); Assert.Equal(image.Url1000, image.Url600); },
            image => { Assert.StartsWith("/files/", image.Url1000); Assert.Equal(image.Url1000, image.Url600); });
        var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("fileId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("derivativeStatus", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audit", json, StringComparison.OrdinalIgnoreCase);
    }
}
