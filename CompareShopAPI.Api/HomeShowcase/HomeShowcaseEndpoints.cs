using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.HomeShowcase;
using Microsoft.Extensions.Caching.Memory;

namespace CompareShopAPI.Api.HomeShowcase;

public static class HomeShowcaseEndpoints
{
    private const string PublicCacheKey = "home-showcase:published:v1";
    private const int PublicCacheSeconds = 3;

    public static RouteGroupBuilder MapHomeShowcaseEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/home-showcase", async (HttpContext context, HomeShowcaseService service, CancellationToken ct) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(await service.GetAdmin(Subject(context), ct));
            }
            catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: 401); }
            catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: 403); }
        });
        admin.MapPut("/home-showcase/draft", async (HttpContext context, HomeShowcaseDraftInput input, HomeShowcaseService service, CancellationToken ct) =>
            await RunAdmin(() => service.ReplaceDraft(Subject(context), input, ct)));
        admin.MapPost("/home-showcase/draft/reset", async (HttpContext context, HomeShowcaseService service, CancellationToken ct) =>
            await RunAdmin(() => service.ResetDraft(Subject(context), ct)));
        admin.MapPost("/home-showcase/publish", async (HttpContext context, HomeShowcaseService service, IMemoryCache cache, CancellationToken ct) =>
            await RunAdmin(() => service.Publish(Subject(context), () => cache.Remove(PublicCacheKey), ct)));
        admin.MapPost("/home-showcase/preview-token", async (HttpContext context, HomeShowcaseService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(new { token = await service.CreatePreviewToken(Subject(context), ct), expiresInSeconds = 600 }); }
            catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: 401); }
            catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: 403); }
            catch (HomeShowcaseRateLimitException ex) { return Results.Problem(ex.Message, statusCode: 429); }
        });
        return admin;
    }

    public static RouteGroupBuilder MapPublicHomeShowcaseEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/home/showcase", async (HttpContext context, HomeShowcaseService service, IMemoryCache cache, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = $"public, max-age={PublicCacheSeconds}";
            if (!cache.TryGetValue(PublicCacheKey, out HomeShowcasePublicResponse? response))
            {
                response = await service.Public(ct);
                cache.Set(PublicCacheKey, response, TimeSpan.FromSeconds(PublicCacheSeconds));
            }
            return Results.Ok(response);
        }).AllowAnonymous();
        api.MapPost("/home/showcase/preview", async (HttpContext context, HomeShowcasePreviewInput input, HomeShowcaseService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            var response = await service.Preview(input.Token, ct);
            return response is null ? Results.Problem("Preview token is invalid or expired.", statusCode: 401) : Results.Ok(response);
        }).AllowAnonymous();
        return api;
    }

    private static string? Subject(HttpContext context) => context.User.FindFirst("sub")?.Value;

    private static async Task<IResult> RunAdmin<T>(Func<Task<T>> operation)
    {
        try { return Results.Ok(await operation()); }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: 401); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: 403); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] }); }
    }
}

public sealed record HomeShowcasePreviewInput(string? Token);
