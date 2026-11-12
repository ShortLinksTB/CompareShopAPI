using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class AdminProductImageMappingTests
{
    [Fact]
    public async Task ProductAndProductListDeriveStandardImageFromFilesUrl()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CompareShopDbContext(options);
        var category = new Category { Slug = "category", Name = "Category", Level = 2 };
        db.Categories.Add(category);
        var products = new[]
        {
            new Product { Slug = "without-image", Name = "Without image", Category = category, ImageUrl = null },
            new Product { Slug = "external-image", Name = "External image", Category = category, ImageUrl = "https://images.example.test/product.jpg" },
            new Product { Slug = "files-image", Name = "Files image", Category = category, ImageUrl = "/files/01234567-89ab-cdef-0123-456789abcdef" },
            new Product { Slug = "malformed-files-image", Name = "Malformed Files image", Category = category, ImageUrl = "/files/gggggggg-gggg-gggg-gggg-gggggggggggg" }
        };
        db.Products.AddRange(products);
        await db.SaveChangesAsync();

        var data = new EfAdminData(db, TimeProvider.System);
        var list = await data.Products(1, 10, null, default);

        Assert.Collection(list.Items,
            item => Assert.True(item.NeedsStandardImage),
            item => Assert.True(item.NeedsStandardImage),
            item => Assert.False(item.NeedsStandardImage),
            item => Assert.True(item.NeedsStandardImage));
        var activeList = await data.Products(1, 10, true, default);
        Assert.Equal(4, activeList.TotalItems);
        foreach (var (product, expected) in products.Zip(new[] { true, true, false, true }))
        {
            var read = await data.Product(product.Id, default);
            Assert.Equal(expected, read!.NeedsStandardImage);
            var seoRead = await data.SeoProduct(product.Id, default);
            Assert.Equal(expected, seoRead!.NeedsStandardImage);
        }
        Assert.Null(await data.Product(int.MaxValue, default));
        Assert.Null(await data.SeoProduct(int.MaxValue, default));
    }
}
