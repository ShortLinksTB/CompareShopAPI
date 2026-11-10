using System.Security.Claims;
using CompareShopAPI.Application.Files;

namespace CompareShopAPI.Api.Files;

public static class FileEndpoints
{
    public static RouteGroupBuilder MapAdminFileEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/files", async (HttpContext context, int? page, int? pageSize, string? q, string? type,
            FileService service, CancellationToken ct) =>
        {
            try { return Results.Ok(await service.List(Subject(context), page ?? 1, pageSize ?? 20, q, type, ct)); }
            catch (Exception ex) { return Error(ex); }
        });
        admin.MapGet("/files/{id:guid}", async (HttpContext context, Guid id, FileService service, CancellationToken ct) =>
        {
            try { return await service.Get(Subject(context), id, ct) is { } file ? Results.Ok(file) : Results.NotFound(); }
            catch (Exception ex) { return Error(ex); }
        });
        admin.MapPost("/files", async (HttpContext context, IFormFile? file, FileService service, CancellationToken ct) =>
        {
            if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["A file is required."] });
            try
            {
                await using var stream = file.OpenReadStream();
                var uploaded = await service.Upload(Subject(context), new(file.FileName, file.ContentType, file.Length, stream), ct);
                return Results.Json(uploaded, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex) { return Error(ex); }
        }).DisableAntiforgery();
        admin.MapDelete("/files/{id:guid}", async (HttpContext context, Guid id, FileService service, CancellationToken ct) =>
        {
            try { return await service.Delete(Subject(context), id, ct) ? Results.NoContent() : Results.NotFound(); }
            catch (Exception ex) { return Error(ex); }
        });
        return admin;
    }

    public static RouteGroupBuilder MapPublicFileEndpoints(this RouteGroupBuilder files)
    {
        files.MapGet("/{id:guid}", async (Guid id, HttpContext context, FileService service, CancellationToken ct) =>
        {
            try
            {
                var target = await service.PublicObjectUrl(id, ct);
                if (target is null) return Results.NotFound();
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                return Results.Redirect(target, permanent: false, preserveMethod: false);
            }
            catch (Exception ex) { return Error(ex); }
        });
        files.MapGet("/objects/{id:guid}", async (Guid id, HttpContext context, FileService service, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var value = await service.GetPublicObject(id, ct);
                if (value is null) return Results.NotFound();
                if (value.ContentType == "application/octet-stream")
                    context.Response.Headers.ContentDisposition = $"attachment; filename=\"{id:D}.svg\"";
                return Results.Stream(value.Content, value.ContentType);
            }
            catch (Exception ex) { return Error(ex); }
        });
        return files;
    }

    private static string? Subject(HttpContext context) => context.User.FindFirst("sub")?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    private static IResult Error(Exception ex) => ex switch
    {
        CompareShopAPI.Application.Admin.AdminUnauthorizedException => Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized),
        CompareShopAPI.Application.Admin.AdminForbiddenException => Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden),
        KeyNotFoundException => Results.NotFound(),
        ArgumentException => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }),
        _ => Results.Problem("An unexpected error occurred.", statusCode: StatusCodes.Status500InternalServerError)
    };
}
