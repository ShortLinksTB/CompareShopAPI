using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;

namespace CompareShopAPI.Tests;

internal static class CategoryTestData
{
    public static async Task<Category> AddLeafAsync(CompareShopDbContext db, string slug, string name, CancellationToken ct = default)
    {
        var vietnam = await CountryTestCompatibility.EnsureVnCountryAsync(db, ct);
        var parent = new Category
        {
            Slug = $"test-parent-{Guid.NewGuid():N}",
            Name = "Test parent category",
            CountryId = vietnam.Id,
            Level = 1
        };
        db.Categories.Add(parent);
        await db.SaveChangesAsync(ct);

        var leaf = new Category
        {
            Slug = slug,
            Name = name,
            CountryId = vietnam.Id,
            ParentCategoryId = parent.Id,
            Level = 2
        };
        db.Categories.Add(leaf);
        await db.SaveChangesAsync(ct);
        return leaf;
    }
}
