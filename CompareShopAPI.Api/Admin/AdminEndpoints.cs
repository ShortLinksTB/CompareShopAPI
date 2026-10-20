using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Ingestion;

namespace CompareShopAPI.Api.Admin;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        admin.MapGet("/me/permissions", (HttpContext c, AdminService s, CancellationToken ct) => Run(() => s.MyPermissions(Subject(c), ct)));
        admin.MapPost("/bootstrap/first-admin", (HttpContext c, AdminService s, CancellationToken ct) => Run(async () =>
        {
            await s.BootstrapFirstAdmin(Subject(c), ct);
            return Results.Ok(new { status = "bootstrapped" });
        }));
        admin.MapGet("/products", (HttpContext c, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.Products(Subject(c), page ?? 1, pageSize ?? 20, ct)));
        admin.MapGet("/products/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => await s.Product(Subject(c), id, ct) is { } x ? Results.Ok(x) : Results.NotFound()));
        admin.MapPost("/products", (HttpContext c, ProductInput input, AdminService s, CancellationToken ct) => Run(() => s.CreateProduct(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapPut("/products/{id:int}", (HttpContext c, int id, ProductInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateProduct(Subject(c), id, input, ct)));
        admin.MapDelete("/products/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => { await s.DeleteProduct(Subject(c), id, ct); return Results.NoContent(); }));

        admin.MapGet("/categories", (HttpContext c, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.Categories(Subject(c), page ?? 1, pageSize ?? 20, ct)));
        admin.MapGet("/categories/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => await s.Category(Subject(c), id, ct) is { } x ? Results.Ok(x) : Results.NotFound()));
        admin.MapPost("/categories", (HttpContext c, CategoryInput input, AdminService s, CancellationToken ct) => Run(() => s.CreateCategory(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapPut("/categories/{id:int}", (HttpContext c, int id, CategoryInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateCategory(Subject(c), id, input, ct)));
        admin.MapDelete("/categories/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => { await s.DeleteCategory(Subject(c), id, ct); return Results.NoContent(); }));

        admin.MapGet("/shops", (HttpContext c, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.Shops(Subject(c), page ?? 1, pageSize ?? 20, ct)));
        admin.MapGet("/shops/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => await s.Shop(Subject(c), id, ct) is { } x ? Results.Ok(x) : Results.NotFound()));
        admin.MapPost("/shops", (HttpContext c, ShopInput input, AdminService s, CancellationToken ct) => Run(() => s.CreateShop(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapPut("/shops/{id:int}", (HttpContext c, int id, ShopInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateShop(Subject(c), id, input, ct)));
        admin.MapDelete("/shops/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => { await s.DeleteShop(Subject(c), id, ct); return Results.NoContent(); }));

        admin.MapGet("/offers", (HttpContext c, AdminService s, CancellationToken ct) => Run(() => s.Offers(Subject(c), ct)));
        admin.MapPatch("/offers/{id:int}", (HttpContext c, int id, OfferInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateOffer(Subject(c), id, input, ct)));
        admin.MapPost("/offers/{id:int}/approve", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(() => s.ApproveOffer(Subject(c), id, ct)));
        admin.MapGet("/matching", (HttpContext c, AdminService s, CancellationToken ct) => Run(() => s.MatchingList(Subject(c), ct)));
        admin.MapPost("/matching/{id:int}/approve", (HttpContext c, int id, MatchingApproval input, AdminService s, CancellationToken ct) => Run(() => s.ApproveMatching(Subject(c), id, input.ProductId, ct)));
        admin.MapGet("/sync-jobs", async (HttpContext c, IngestionAdminService s, CancellationToken ct) =>
        {
            try { return Results.Ok(await s.List(Subject(c), ct)); }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
        });
        admin.MapPost("/sync-jobs/{id:int}/retry", async (HttpContext c, int id, IngestionAdminService s, CancellationToken ct) =>
        {
            try { return Results.Accepted(value: await s.Retry(Subject(c), id, ct)); }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        admin.MapGet("/audit-logs", (HttpContext c, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.AuditLogs(Subject(c), page ?? 1, pageSize ?? 50, ct)));
        admin.MapGet("/dashboard/summary", (HttpContext c, AdminService s, CancellationToken ct) => Run(() => s.Dashboard(Subject(c), ct)));
        admin.MapGet("/price-alerts", (HttpContext c, int? page, int? pageSize, int? productId, int? userId, AdminService s, CancellationToken ct) => Run(() => s.PriceAlerts(Subject(c), page ?? 1, pageSize ?? 50, productId, userId, ct)));
        admin.MapGet("/analytics", (HttpContext c, AdminService s, CancellationToken ct) => Run(() => s.Analytics(Subject(c), ct)));
        admin.MapGet("/seo/{resourceType}/{id:int}", (HttpContext c, string resourceType, int id, AdminService s, CancellationToken ct) => Run(async () =>
        {
            if (!resourceType.Equals("product", StringComparison.OrdinalIgnoreCase)) { await s.CheckSeoUnavailable(Subject(c), false, ct); return Results.NotFound(new { error = "resource_unavailable" }); }
            return await s.SeoProduct(Subject(c), id, ct) is { } product ? Results.Ok(new { product.Id, product.SeoTitle, product.SeoDescription }) : Results.NotFound();
        }));
        admin.MapPatch("/seo/{resourceType}/{id:int}", (HttpContext c, string resourceType, int id, SeoInput input, AdminService s, CancellationToken ct) => Run(async () =>
        {
            if (!resourceType.Equals("product", StringComparison.OrdinalIgnoreCase)) { await s.CheckSeoUnavailable(Subject(c), true, ct); return Results.NotFound(new { error = "resource_unavailable" }); }
            return Results.Ok(await s.SaveSeoProduct(Subject(c), id, input, ct));
        }));
        admin.MapGet("/users", (HttpContext c, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.Users(Subject(c), page ?? 1, pageSize ?? 50, ct)));
        admin.MapPut("/users/{id:int}/role-assignments", (HttpContext c, int id, RoleAssignmentsInput input, AdminService s, CancellationToken ct) => Run(() => s.ReplaceRoles(Subject(c), id, input.RoleIds, ct)));
        admin.MapGet("/feature-flags", (HttpContext c, AdminService s, CancellationToken ct) => Run(() => s.FeatureFlags(Subject(c), ct)));
        admin.MapPatch("/feature-flags/{key}", (HttpContext c, string key, FeatureFlagInput input, AdminService s, CancellationToken ct) => Run(() => s.SaveFeatureFlag(Subject(c), key, input, ct)));
        admin.MapGet("/settings", (HttpContext c, AdminService s, CancellationToken ct) => Run(() => s.Settings(Subject(c), ct)));
        admin.MapPatch("/settings", (HttpContext c, Dictionary<string, string> input, AdminService s, CancellationToken ct) => Run(() => s.SaveSettings(Subject(c), input, ct)));
        admin.MapGet("/compliance/bans", (HttpContext c, string? targetType, bool? active, string? search, int? page, int? pageSize, BannedItemService s, CancellationToken ct) =>
            RunCompliance(() => s.List(Subject(c), targetType, active, search, page ?? 1, pageSize ?? 20, ct)));
        admin.MapGet("/compliance/bans/{id:int}", (HttpContext c, int id, BannedItemService s, CancellationToken ct) =>
            RunCompliance(async () => await s.Get(Subject(c), id, ct) is { } item ? Results.Ok(item) : Results.NotFound()));
        admin.MapPost("/compliance/bans", (HttpContext c, BannedItemInput input, BannedItemService s, CancellationToken ct) =>
            RunCompliance(() => s.Create(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapDelete("/compliance/bans/{id:int}", (HttpContext c, int id, BannedItemService s, CancellationToken ct) =>
            RunCompliance(async () => await s.Revoke(Subject(c), id, ct) ? Results.NoContent() : Results.NotFound()));
        return admin;
    }

    private static string? Subject(HttpContext context) => context.User.FindFirst("sub")?.Value;
    private static async Task<IResult> Run<T>(Func<Task<T>> operation, int successStatus = StatusCodes.Status200OK)
    {
        try { var result = await operation(); return successStatus == StatusCodes.Status201Created ? Results.Json(result, statusCode: successStatus) : Results.Ok(result); }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (AdminBootstrapCompletedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    }
    private static async Task<IResult> Run(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (AdminBootstrapCompletedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    }
    private static async Task<IResult> RunCompliance<T>(Func<Task<T>> operation, int successStatus = StatusCodes.Status200OK)
    {
        try
        {
            var result = await operation();
            return successStatus == StatusCodes.Status201Created ? Results.Json(result, statusCode: successStatus) : Results.Ok(result);
        }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (BannedItemNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (BannedItemConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    }
    private static async Task<IResult> RunCompliance(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (BannedItemNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (BannedItemConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    }
    private static async Task<IResult> Run(Func<Task> operation)
    {
        try { await operation(); return Results.NoContent(); }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (AdminBootstrapCompletedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    }
}

public sealed record MatchingApproval(int ProductId);
public sealed record RoleAssignmentsInput(IReadOnlyList<int> RoleIds);
