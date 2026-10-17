using CompareShopAPI.Application.Catalog;

namespace CompareShopAPI.Api.Catalog;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/categories", async (CatalogService service, CancellationToken ct) =>
            Results.Ok(new { items = await service.Categories(ct) })).AllowAnonymous();
        api.MapGet("/products", async (int? page, int? pageSize, int? categoryId, int? minPrice, int? maxPrice, int? shopId, string? search, CatalogService service, CancellationToken ct) =>
        {
            var filter = new ProductFilter(page ?? 1, pageSize ?? 20, categoryId, minPrice, maxPrice, shopId, search);
            try { return Results.Ok(await service.Products(filter, ct)); }
            catch (ArgumentOutOfRangeException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/search/suggestions", async (string? q, int? limit, CatalogService service, CancellationToken ct) =>
        {
            try { return Results.Ok(new { items = await service.Suggestions(q, limit ?? 8, ct) }); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/products/{id:int}/price-history", async (int id, string? from, string? to, PriceHistoryService service, CancellationToken ct) =>
        {
            try
            {
                var history = await service.History(id, from, to, ct);
                return history is null ? Results.NotFound() : Results.Ok(new { items = history });
            }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [ex.Message] }); }
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
