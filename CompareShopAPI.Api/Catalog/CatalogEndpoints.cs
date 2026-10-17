using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Api.RateLimiting;

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
        api.MapGet("/go/{offerId:int}", async (int offerId, HttpContext context, AffiliateRedirectService service, IClientIpResolver ipResolver, IAffiliateRedirectRateLimiter rateLimiter, CancellationToken ct, ILoggerFactory loggerFactory) =>
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
            var result = await service.Redirect(offerId, clientIp, ct);
            if (result.TrackingFailed)
            {
                loggerFactory.CreateLogger("AffiliateRedirect").LogError("Affiliate click tracking failed for offer {OfferId}.", offerId);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            return result.Found && result.Destination is not null
                ? Results.Redirect(result.Destination.AbsoluteUri, permanent: false)
                : Results.NotFound();
        }).AllowAnonymous();
        return api;
    }
}
