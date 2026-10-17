using CompareShopAPI.Application.Catalog;

namespace CompareShopAPI.Api.Catalog;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/categories", async (CatalogService service, CancellationToken ct) =>
            Results.Ok(new { items = await service.Categories(ct) })).AllowAnonymous();
        api.MapGet("/products", async (int? page, int? pageSize, int? categoryId, int? minPrice, int? maxPrice, int? shopId, CatalogService service, CancellationToken ct) =>
        {
            var filter = new ProductFilter(page ?? 1, pageSize ?? 20, categoryId, minPrice, maxPrice, shopId);
            try { return Results.Ok(await service.Products(filter, ct)); }
            catch (ArgumentOutOfRangeException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = ["Use page >= 1, pageSize 1..100, non-negative prices, and minPrice <= maxPrice."] }); }
        }).AllowAnonymous();
        api.MapGet("/products/{slug}", async (string slug, CatalogService service, CancellationToken ct) =>
        {
            var product = await service.Product(slug, ct);
            return product is null ? Results.NotFound() : Results.Ok(product);
        }).AllowAnonymous();
        api.MapGet("/products/{id:int}/offers", async (int id, CatalogService service, CancellationToken ct) =>
        {
            var offers = await service.Offers(id, ct);
            return offers is null ? Results.NotFound() : Results.Ok(new { items = offers });
        }).AllowAnonymous();
        api.MapGet("/shops/{slug}", async (string slug, CatalogService service, CancellationToken ct) =>
        {
            var shop = await service.Shop(slug, ct);
            return shop is null ? Results.NotFound() : Results.Ok(shop);
        }).AllowAnonymous();
        return api;
    }
}
