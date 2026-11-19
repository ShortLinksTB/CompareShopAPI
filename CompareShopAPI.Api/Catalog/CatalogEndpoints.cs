using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Api.RateLimiting;

namespace CompareShopAPI.Api.Catalog;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/countries", async (ICountryCatalog countries, CancellationToken ct) =>
            Results.Ok(new { items = await countries.ActiveCountries(ct) })).AllowAnonymous();
        api.MapGet("/partners", async (HttpContext context, string? country, CatalogService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=30, stale-while-revalidate=30";
            try { return Results.Ok(await service.HomePartners(country, ct)); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/categories", async (int? parentId, string? country, CatalogService service, CancellationToken ct) =>
        {
            try { return Results.Ok(new { items = await service.Categories(country, parentId, ct) }); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/products", async (int? page, int? pageSize, int? categoryId, int? minPrice, int? maxPrice, int? shopId, string? search, string? country, CatalogService service, CancellationToken ct) =>
        {
            var filter = new ProductFilter(page ?? 1, pageSize ?? 20, categoryId, minPrice, maxPrice, shopId, search);
            try { return Results.Ok(await service.Products(filter, country, ct)); }
            catch (ArgumentOutOfRangeException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [ex.Message] }); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/search/suggestions", async (string? q, int? limit, string? country, CatalogService service, CancellationToken ct) =>
        {
            try { return Results.Ok(new { items = await service.Suggestions(country, q, limit ?? 8, ct) }); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [ex.Message] }); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
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
        api.MapGet("/products/{slug}", async (string slug, string? country, CatalogService service, CancellationToken ct) =>
        {
            try { var product = await service.Product(country, slug, ct); return product is null ? Results.NotFound() : Results.Ok(product); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/products/{id:int}/offers", async (int id, string? country, CatalogService service, CancellationToken ct) =>
        {
            try { var offers = await service.Offers(country, id, ct); return offers is null ? Results.NotFound() : Results.Ok(new { items = offers }); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/shops/{slug}", async (string slug, string? country, CatalogService service, CancellationToken ct) =>
        {
            try { var shop = await service.Shop(country, slug, ct); return shop is null ? Results.NotFound() : Results.Ok(shop); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
        }).AllowAnonymous();
        api.MapGet("/go/{offerId:int}", async (int offerId, string? country, HttpContext context, CountrySelectionService countries, AffiliateRedirectService service, IClientIpResolver ipResolver, IAffiliateRedirectRateLimiter rateLimiter, CancellationToken ct, ILoggerFactory loggerFactory) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            var clientIp = ipResolver.Resolve(context);
            if (clientIp is not null)
            {
                var decision = await rateLimiter.TryAcquireAsync(clientIp, ct);
                if (decision.IsRejected)
                {
                    context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Json(new { error = "rate_limit_exceeded" }, statusCode: StatusCodes.Status429TooManyRequests);
                }
            }
            int countryId;
            try { countryId = await countries.Resolve(country, ct); }
            catch (CountryUnavailableException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["country"] = [ex.Message] }); }
            var result = await service.Redirect(countryId, offerId, clientIp, ct);
            if (result.TrackingFailed)
            {
                loggerFactory.CreateLogger("AffiliateRedirect").LogError("Affiliate click tracking failed for offer {OfferId}.", offerId);
                loggerFactory.CreateLogger("AffiliateRedirect").LogWarning("Affiliate click was not recorded for offer {OfferId}; redirect continues.", offerId);
            }
            return result.Found && result.Destination is not null
                ? Results.Redirect(result.Destination.AbsoluteUri, permanent: false)
                : Results.NotFound();
        }).AllowAnonymous();
        return api;
    }
}
