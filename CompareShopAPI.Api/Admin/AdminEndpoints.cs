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
        admin.MapGet("/products", (HttpContext c, int? page, int? pageSize, bool? isActive, AdminService s, CancellationToken ct) => Run(() => s.Products(Subject(c), page ?? 1, pageSize ?? 20, isActive, ct)));
        admin.MapGet("/products/slug-availability", (HttpContext c, string? slug, AdminService s, CancellationToken ct) => Run(() => s.ProductSlugAvailability(Subject(c), slug, ct)));
        admin.MapGet("/products/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => await s.Product(Subject(c), id, ct) is { } x ? Results.Ok(x) : Results.NotFound()));
        admin.MapGet("/products/{id:int}/images", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => await s.ProductImages(Subject(c), id, ct) is { } x ? Results.Ok(new { items = x }) : Results.NotFound()));
        admin.MapPut("/products/{id:int}/images", (HttpContext c, int id, ProductImageInput[] input, bool? createDerivatives, AdminService s, CancellationToken ct) => Run(async () => await s.ReplaceProductImages(Subject(c), id, input, ct, createDerivatives ?? true) is { } x ? Results.Ok(new { items = x }) : Results.NotFound()));
        admin.MapPost("/products/{id:int}/images/{imageId:guid}/derivatives", (HttpContext c, int id, Guid imageId, AdminService s, CancellationToken ct) => Run(async () => await s.RetryProductImageDerivatives(Subject(c), id, imageId, ct) is { } x ? Results.Ok(x) : Results.NotFound()));
        admin.MapPost("/products", (HttpContext c, ProductInput input, AdminService s, CancellationToken ct) => Run(() => s.CreateProduct(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapPut("/products/{id:int}", (HttpContext c, int id, ProductInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateProduct(Subject(c), id, input, ct)));
        admin.MapDelete("/products/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => { await s.DeleteProduct(Subject(c), id, ct); return Results.NoContent(); }));

        admin.MapGet("/categories", (HttpContext c, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.Categories(Subject(c), page ?? 1, pageSize ?? 20, ct)));
        admin.MapGet("/categories/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => await s.Category(Subject(c), id, ct) is { } x ? Results.Ok(x) : Results.NotFound()));
        admin.MapPost("/categories", (HttpContext c, CategoryInput input, AdminService s, CancellationToken ct) => Run(() => s.CreateCategory(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapPut("/categories/{id:int}", (HttpContext c, int id, CategoryInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateCategory(Subject(c), id, input, ct)));
        admin.MapDelete("/categories/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => { await s.DeleteCategory(Subject(c), id, ct); return Results.NoContent(); }));
        admin.MapGet("/affiliate-platforms", (HttpContext c, AffiliatePlatformService s, CancellationToken ct) => RunAffiliate(() => s.List(Subject(c), ct)));
        admin.MapPost("/affiliate-platforms", (HttpContext c, AffiliatePlatformInput input, AffiliatePlatformService s, CancellationToken ct) => RunAffiliate(() => s.Create(Subject(c), input, ct)));
        admin.MapPut("/affiliate-platforms/{id:int}", (HttpContext c, int id, AffiliatePlatformInput input, AffiliatePlatformService s, CancellationToken ct) =>
            RunAffiliate(async () => await s.Update(Subject(c), id, input, ct) is { } item ? Results.Ok(item) : Results.NotFound()));
        admin.MapDelete("/affiliate-platforms/{id:int}", (HttpContext c, int id, AffiliatePlatformService s, CancellationToken ct) =>
            RunAffiliate(async () => await s.Delete(Subject(c), id, ct) ? Results.NoContent() : Results.NotFound()));
        admin.MapPost("/affiliate-platforms/bulk-delete", (HttpContext c, AffiliatePlatformBulkDeleteInput input, AffiliatePlatformService s, CancellationToken ct) =>
            RunAffiliate(() => s.BulkDelete(Subject(c), input.Ids, ct)));
        admin.MapPost("/affiliate-platforms/import/preview", (HttpContext c, AffiliatePlatformInput[] input, AffiliatePlatformService s, CancellationToken ct) =>
            RunAffiliate(() => s.Preview(Subject(c), input, ct)));
        admin.MapPost("/affiliate-platforms/import/commit", (HttpContext c, AffiliatePlatformInput[] input, AffiliatePlatformService s, CancellationToken ct) =>
            RunAffiliate(() => s.Commit(Subject(c), input, ct)));
        admin.MapGet("/categories/export", (HttpContext context, CategoryImportService service, CancellationToken ct) =>
            RunCategoryExport(context, service, ct));
        admin.MapPost("/categories/import/dry-run", async (HttpContext context, CategoryImportService service, CancellationToken ct) =>
            await RunCategoryImport(context, service, ct, commit: false)).DisableAntiforgery();
        admin.MapPost("/categories/import/commit", async (HttpContext context, CategoryImportService service, CancellationToken ct) =>
            await RunCategoryImport(context, service, ct, commit: true)).DisableAntiforgery();

        admin.MapGet("/shops", (HttpContext c, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.Shops(Subject(c), page ?? 1, pageSize ?? 20, ct)));
        admin.MapGet("/shops/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => await s.Shop(Subject(c), id, ct) is { } x ? Results.Ok(x) : Results.NotFound()));
        admin.MapPost("/shops", (HttpContext c, ShopInput input, AdminService s, CancellationToken ct) => Run(() => s.CreateShop(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapPut("/shops/{id:int}", (HttpContext c, int id, ShopInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateShop(Subject(c), id, input, ct)));
        admin.MapDelete("/shops/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(async () => { await s.DeleteShop(Subject(c), id, ct); return Results.NoContent(); }));

        admin.MapGet("/offers", (HttpContext c, int? page, int? pageSize, string? q, int? shopId, int? productId, string? filter, string? sort, int? staleHours, AdminService s, CancellationToken ct) =>
            Run(() => s.Offers(Subject(c), new(page ?? 1, pageSize ?? 25, q, shopId, productId, filter, sort ?? "attention", staleHours ?? 72), ct)));
        admin.MapGet("/offers/{id:int}/price-history", (HttpContext c, int id, int? days, AdminService s, CancellationToken ct) =>
            Run(async () => await s.OfferHistory(Subject(c), id, days ?? 30, ct) is { } points ? Results.Ok(points) : Results.NotFound()));
        admin.MapPost("/offers", async (HttpContext c, OfferImportInput input, AdminService s, CancellationToken ct) =>
        {
            try
            {
                var result = await s.ImportOffer(Subject(c), input, ct);
                return Results.Json(result.Offer, statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            }
            catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
            catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
            catch (AdminOfferImportConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
        });
        admin.MapPatch("/offers/{id:int}", (HttpContext c, int id, OfferInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateOffer(Subject(c), id, input, ct)));
        admin.MapPatch("/offers/{id:int}/outlier-reason", (HttpContext c, int id, OutlierReasonInput input, AdminService s, CancellationToken ct) => Run(() => s.UpdateOfferOutlierReason(Subject(c), id, input.OutlierReason, ct)));
        admin.MapPost("/offers/{id:int}/approve", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(() => s.ApproveOffer(Subject(c), id, ct)));
        admin.MapGet("/matching", (HttpContext c, string? status, string? verdict, string? q, int? page, int? pageSize, AdminService s, CancellationToken ct) => Run(() => s.MatchingPage(Subject(c), new(status, verdict, q, page ?? 1, pageSize ?? 25), ct)));
        admin.MapPost("/matching/{id:int}/approve", (HttpContext c, int id, MatchingApproval input, AdminService s, CancellationToken ct) => Run(() => s.ApproveMatching(Subject(c), id, input.ProductId, ct)));
        admin.MapPost("/matching/approve-group", (HttpContext c, MatchingGroupApproval input, AdminService s, CancellationToken ct) => Run(() => s.ApproveMatchingGroup(Subject(c), input.Ids, input.ProductId, ct)));
        admin.MapPost("/matching/{id:int}/create-product", (HttpContext c, int id, MatchingProductCreate input, AdminService s, CancellationToken ct) => Run(() => s.CreateMatchingProduct(Subject(c), id, input, ct), StatusCodes.Status201Created));
        admin.MapPost("/matching/{id:int}/skip", (HttpContext c, int id, MatchingSkip input, AdminService s, CancellationToken ct) => Run(() => s.SkipMatching(Subject(c), id, input.Reason, input.Note, ct)));
        admin.MapPost("/matching/{id:int}/restore", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(() => s.RestoreMatching(Subject(c), id, ct)));
        admin.MapGet("/products/{id:int}/matchings", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(() => s.ProductMatchings(Subject(c), id, ct)));
        admin.MapDelete("/matching/{id:int}", (HttpContext c, int id, AdminService s, CancellationToken ct) => Run(() => s.UnlinkMatching(Subject(c), id, ct)));
        admin.MapGet("/data-sources", async (HttpContext c, DataSourceAdminService s, CancellationToken ct) =>
        {
            try { return Results.Ok(await s.List(Subject(c), ct)); }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
        });
        admin.MapPatch("/data-sources/{id:int}", async (HttpContext c, int id, DataSourceConfigurationRequest request, DataSourceAdminService s, CancellationToken ct) =>
        {
            try { return Results.Ok(await s.Configure(Subject(c), id, request.ToInput(), ct)); }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
        });
        admin.MapPost("/data-sources/{id:int}/sync", async (HttpContext c, int id, DataSourceAdminService s, CancellationToken ct) =>
        {
            try { return Results.Accepted(value: await s.QueueSync(Subject(c), id, ct)); }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (DataSourceSyncConflictException) { return Results.Problem("Data source is inactive, in Error, or already has an active sync job.", statusCode: StatusCodes.Status409Conflict); }
            catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
        });
        admin.MapSyncJobEndpoints();
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

    public static RouteGroupBuilder MapSyncJobEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/sync-jobs", async (HttpContext c, int? page, int? pageSize, string? q, DateTime? startedFrom, DateTime? startedTo, IngestionAdminService s, CancellationToken ct) =>
        {
            try
            {
                if (c.Request.Query.Count == 0) return Results.Ok(await s.List(Subject(c), ct));
                return Results.Ok(await s.Search(Subject(c), page ?? 1, pageSize ?? 25, q, c.Request.Query["status"].ToString(), startedFrom, startedTo, ct));
            }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        });
        admin.MapGet("/sync-jobs/{id:int}/logs", async (HttpContext c, int id, int? page, int? pageSize, IngestionAdminService s, CancellationToken ct) =>
        {
            try { return Results.Ok(await s.Logs(Subject(c), id, page ?? 1, pageSize ?? 100, ct)); }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        });
        admin.MapPost("/sync-jobs/{id:int}/retry", async (HttpContext c, int id, IngestionAdminService s, CancellationToken ct) =>
        {
            try { return Results.Accepted(value: await s.Retry(Subject(c), id, ct)); }
            catch (IngestionForbiddenException) { return Results.Problem("Forbidden", statusCode: StatusCodes.Status403Forbidden); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        return admin;
    }

    private static string? Subject(HttpContext context) => context.User.FindFirst("sub")?.Value;
    public static async Task<IResult> RunCategoryExport(HttpContext context, CategoryImportService service, CancellationToken ct)
    {
        try
        {
            var document = await service.Export(Subject(context), ct);
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var filename = $"categories-{document.ExportedAt.UtcDateTime:yyyy-MM-dd}.json";
            return Results.File(json, "application/json", filename);
        }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
    }

    public static async Task<IResult> RunCategoryImport(HttpContext context, CategoryImportService service, CancellationToken ct, bool commit)
    {
        try
        {
            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A JSON file is required."] }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (file.Length > CategoryImportService.MaximumFileBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, ct);
            if (buffer.Length > CategoryImportService.MaximumFileBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var content = buffer.ToArray();
            if (!commit)
            {
                var preview = await service.DryRun(Subject(context), content, ct);
                return Results.Json(preview, statusCode: preview.Valid ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
            }
            var result = await service.Commit(Subject(context), file.FileName, form["confirmation"].ToString(), content, ct);
            return Results.Ok(result);
        }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (CategoryImportFileTooLargeException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (CategoryImportValidationException ex) { return Results.Json(new CategoryImportPreview(false, false, ex.Errors, [], new(0, 0, 0), []), statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (CategoryImportConflictException ex) { return Results.Json(new { code = "category_in_use", blockers = ex.Blockers }, statusCode: StatusCodes.Status409Conflict); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }, statusCode: StatusCodes.Status422UnprocessableEntity); }
    }
    private static async Task<IResult> Run<T>(Func<Task<T>> operation, int successStatus = StatusCodes.Status200OK)
    {
        try { var result = await operation(); return successStatus == StatusCodes.Status201Created ? Results.Json(result, statusCode: successStatus) : Results.Ok(result); }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (ProductImageFilesUnavailableException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable); }
        catch (ProductImageValidationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["images"] = [ex.Message] }, statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (AdminBootstrapCompletedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (MatchingLaptopCategoryUnavailableException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = "category_unavailable" }); }
        catch (MatchingProductSlugConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = "slug_conflict", ["slug"] = ex.Slug }); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    }
    private static async Task<IResult> RunAffiliate<T>(Func<Task<T>> operation)
    {
        try
        {
            var result = await operation();
            return result is IResult httpResult ? httpResult : Results.Ok(result);
        }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (AffiliatePlatformDuplicateException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (KeyNotFoundException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }); }
    }
    private static async Task<IResult> Run(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (AdminUnauthorizedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized); }
        catch (AdminForbiddenException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden); }
        catch (ProductImageFilesUnavailableException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable); }
        catch (ProductImageValidationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["images"] = [ex.Message] }, statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (AdminBootstrapCompletedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (ProductDeleteConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (MatchingLaptopCategoryUnavailableException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = "category_unavailable" }); }
        catch (MatchingProductSlugConflictException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["code"] = "slug_conflict", ["slug"] = ex.Slug }); }
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

public sealed record OutlierReasonInput(string? OutlierReason);
public sealed record DataSourceConfigurationRequest(string State, int IntervalHours, int? PartnerShopId)
{
    public DataSourceConfigurationInput ToInput()
    {
        if (!Enum.TryParse<CompareShopAPI.Domain.Ingestion.DataSourceState>(State, ignoreCase: true, out var parsed))
            throw new ArgumentException("State must be Active or Paused.", nameof(State));
        return new(parsed, IntervalHours, PartnerShopId);
    }
}

public sealed record MatchingApproval(int ProductId);
public sealed record MatchingGroupApproval(IReadOnlyList<int> Ids, int ProductId);
public sealed record MatchingSkip(string Reason, string? Note);
public sealed record RoleAssignmentsInput(IReadOnlyList<int> RoleIds);
