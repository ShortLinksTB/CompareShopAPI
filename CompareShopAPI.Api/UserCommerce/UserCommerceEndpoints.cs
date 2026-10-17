using System.Security.Claims;
using CompareShopAPI.Application.UserCommerce;

namespace CompareShopAPI.Api.UserCommerce;

public sealed record WishlistChangeRequest(int ProductId, string Operation);
public sealed record CreatePriceAlertRequest(int ProductId, int TargetPrice);

public static class UserCommerceEndpoints
{
    public static RouteGroupBuilder MapUserCommerceEndpoints(this RouteGroupBuilder api)
    {
        var userApi = api.MapGroup("").RequireAuthorization("AuthenticatedUser");
        userApi.MapGet("/wishlist", async (ClaimsPrincipal principal, WishlistService service, CancellationToken ct) =>
            Results.Ok(new { items = await service.List(principal.FindFirstValue("sub")!, ct) }));
        userApi.MapPost("/wishlist", async (WishlistChangeRequest request, ClaimsPrincipal principal, WishlistService service, TimeProvider clock, CancellationToken ct) =>
        {
            try
            {
                var found = await service.Change(principal.FindFirstValue("sub")!, request.ProductId, request.Operation, clock.GetUtcNow().UtcDateTime, ct);
                return found ? Results.NoContent() : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
        });
        userApi.MapGet("/price-alerts", async (ClaimsPrincipal principal, PriceAlertService service, CancellationToken ct) =>
            Results.Ok(new { items = await service.List(principal.FindFirstValue("sub")!, ct) }));
        userApi.MapPost("/price-alerts", async (CreatePriceAlertRequest request, ClaimsPrincipal principal, PriceAlertService service, TimeProvider clock, CancellationToken ct) =>
        {
            try
            {
                var alert = await service.Create(principal.FindFirstValue("sub")!, request.ProductId, request.TargetPrice, clock.GetUtcNow().UtcDateTime, ct);
                return alert is null ? Results.NotFound() : Results.Created($"/api/v1/price-alerts/{alert.Id}", alert);
            }
            catch (ArgumentOutOfRangeException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
        });
        userApi.MapDelete("/price-alerts/{id:int}", async (int id, ClaimsPrincipal principal, PriceAlertService service, CancellationToken ct) =>
            await service.Delete(principal.FindFirstValue("sub")!, id, ct) ? Results.NoContent() : Results.NotFound());
        return api;
    }
}
