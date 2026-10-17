using System.Security.Claims;
using CompareShopAPI.Application.Users;

namespace CompareShopAPI.Api.UserFeatures;

public sealed record WishlistRequest(int ProductId, string Operation);
public sealed record CreatePriceAlertRequest(int ProductId, int TargetPrice);

public static class UserFeatureEndpoints
{
    public static RouteGroupBuilder MapUserFeatureEndpoints(this RouteGroupBuilder api)
    {
        var authenticated = api.MapGroup("").RequireAuthorization("AuthenticatedUser");

        authenticated.MapGet("/wishlist", async (ClaimsPrincipal principal, UserFeatureService service, CancellationToken ct) =>
            Results.Ok(new { items = await service.Wishlist(Subject(principal), ct) }));
        authenticated.MapPost("/wishlist", async (WishlistRequest request, ClaimsPrincipal principal, UserFeatureService service, CancellationToken ct) =>
        {
            var result = await service.ChangeWishlist(Subject(principal), request.ProductId, request.Operation, ct);
            return result.Error switch
            {
                UserFeatureError.InvalidInput => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Provide a positive productId and operation 'add' or 'remove'."] }),
                UserFeatureError.ProductNotFound => Results.NotFound(),
                _ => Results.NoContent()
            };
        });
        authenticated.MapPost("/price-alerts", async (CreatePriceAlertRequest request, ClaimsPrincipal principal, UserFeatureService service, CancellationToken ct) =>
        {
            var result = await service.CreatePriceAlert(Subject(principal), request.ProductId, request.TargetPrice, ct);
            if (result.Error == UserFeatureError.InvalidInput)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Provide a positive productId and targetPrice."] });
            if (result.Error == UserFeatureError.ProductNotFound) return Results.NotFound();
            return Results.Created($"/api/v1/price-alerts/{result.Value!.Id}", result.Value);
        });
        authenticated.MapGet("/price-alerts", async (ClaimsPrincipal principal, UserFeatureService service, CancellationToken ct) =>
            Results.Ok(new { items = await service.PriceAlerts(Subject(principal), ct) }));
        authenticated.MapDelete("/price-alerts/{id:int}", async (int id, ClaimsPrincipal principal, UserFeatureService service, CancellationToken ct) =>
        {
            var result = await service.DeletePriceAlert(Subject(principal), id, ct);
            return result.Succeeded ? Results.NoContent() : Results.NotFound();
        });
        return api;
    }

    private static string Subject(ClaimsPrincipal principal) => principal.FindFirstValue("sub")!;
}
