using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Tests;

internal static class CountryTestCompatibility
{
    public const int VnCountryId = 1;

    public static Task<IReadOnlyList<CategoryDto>> Categories(this EfCatalogQueries queries, int? parentId, CancellationToken ct) =>
        queries.Categories(VnCountryId, parentId, ct);

    public static Task<ProductDetailDto?> Product(this EfCatalogQueries queries, string slug, CancellationToken ct) =>
        queries.Product(VnCountryId, slug, ct);

    public static Task<IReadOnlyList<OfferDto>?> Offers(this EfCatalogQueries queries, int productId, CancellationToken ct) =>
        queries.Offers(VnCountryId, productId, ct);

    public static Task<ShopDto?> Shop(this EfCatalogQueries queries, string slug, CancellationToken ct) =>
        queries.Shop(VnCountryId, slug, ct);

    public static Task<IReadOnlyList<HomePartnerDto>> HomePartners(this EfCatalogQueries queries, CancellationToken ct) =>
        queries.HomePartners(VnCountryId, ct);

    public static Task<IReadOnlyList<ProductSuggestionDto>> Suggestions(this EfCatalogQueries queries, string query, int limit, CancellationToken ct) =>
        queries.Suggestions(VnCountryId, query, limit, ct);

    public static Task<PageDto<ProductListDto>> Products(this EfCatalogQueries queries, ProductFilter filter, CancellationToken ct) =>
        queries.Products(filter with { CountryId = VnCountryId }, ct);

    public static Task<PageDto<ProductListDto>> Products(this CatalogService service, ProductFilter filter, CancellationToken ct) =>
        service.Products(filter, "VN", ct);

    public static Task<IReadOnlyList<ProductSuggestionDto>> Suggestions(this CatalogService service, string? query, int limit, CancellationToken ct) =>
        service.Suggestions("VN", query, limit, ct);

    public static Task<Page<AdminCategory>> Categories(this AdminService service, string? subject, int page, int size, CancellationToken ct) =>
        service.Categories(subject, VnCountryId, page, size, ct);

    public static Task<AffiliateLink?> FindRedirectableLink(this EfAffiliateRedirectRepository repository, int offerId, CancellationToken ct) =>
        repository.FindRedirectableLink(VnCountryId, offerId, ct);

    private sealed class TestCountryCatalog : ICountryCatalog
    {
        public Task<IReadOnlyList<CountryDto>> ActiveCountries(CancellationToken ct) => Task.FromResult<IReadOnlyList<CountryDto>>([new(VnCountryId, "VN", "Vietnam")]);
        public Task<int?> ActiveCountryId(string code, CancellationToken ct) => Task.FromResult<int?>(code == "VN" ? VnCountryId : null);
    }

    public static CountrySelectionService VnCountrySelection() => new(new TestCountryCatalog());
    public static ICountryCatalog VnCountryCatalog() => new TestCountryCatalog();

    public static async Task<Country> EnsureVnCountryAsync(CompareShopDbContext db, CancellationToken ct = default)
    {
        var country = await db.Countries.SingleOrDefaultAsync(x => x.Code == "VN", ct);
        if (country is not null) return country;
        country = new Country("VN", "Vietnam", true);
        db.Countries.Add(country);
        await db.SaveChangesAsync(ct);
        return country;
    }
}

internal static class MigrationTestHelpers
{
    private const string CountrySchemaStage = "20261118170350_AddCountryAndNullableCatalogCountryIds";
    private const string LegacyCountrySeedStage = "20261118170355_AssignLegacyCatalogSeedRowsToVN";

    public static async Task MigrateToLatestAsync(CompareShopDbContext db)
    {
        var applied = await db.Database.GetAppliedMigrationsAsync();
        if (!applied.Contains(CountrySchemaStage, StringComparer.Ordinal))
            await db.Database.MigrateAsync(CountrySchemaStage);

        await CountrySeed.SeedInitialAsync(db, typeof(CompareShopAPI.Api.Catalog.CatalogSeedCommand).Assembly);
        applied = await db.Database.GetAppliedMigrationsAsync();
        if (!applied.Contains(LegacyCountrySeedStage, StringComparer.Ordinal))
            await db.Database.MigrateAsync(LegacyCountrySeedStage);

        // Existing rows in these SQL integration tests are test fixtures and use the VN catalog.
        // Production migrations intentionally do not make this broad assignment.
        var vnId = await db.Countries.Where(x => x.Code == "VN").Select(x => x.Id).SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [PartnerShops] SET [CountryId] = {vnId} WHERE [CountryId] IS NULL");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Categories] SET [CountryId] = {vnId} WHERE [CountryId] IS NULL");

        await db.Database.MigrateAsync();
    }
}
