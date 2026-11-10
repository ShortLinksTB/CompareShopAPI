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
        admin.MapPost("/files", async (HttpContext context, IFormFileCollection files, FileService service, CancellationToken ct) =>
        {
            try { await service.EnsureWritePermission(Subject(context), ct); }
            catch (Exception ex) { return Error(ex); }
            if (files.Count == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["files"] = ["At least one file is required."] });
            var outcomes = new List<FileUploadOutcome>(files.Count);
            foreach (var file in files)
            {
                try
                {
                    await using var stream = file.OpenReadStream();
                    var uploaded = await service.Upload(Subject(context), new(file.FileName, file.ContentType, file.Length, stream), ct);
                    outcomes.Add(new(file.FileName, uploaded, StatusCodes.Status201Created, null));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var status = UploadStatus(ex);
                    outcomes.Add(new(file.FileName, null, status, status == StatusCodes.Status500InternalServerError
                        ? "An unexpected error occurred."
                        : ex.Message));
                }
            }
            return Results.Ok(outcomes);
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

    private static int UploadStatus(Exception ex) => ex switch
    {
        CompareShopAPI.Application.Admin.AdminUnauthorizedException => StatusCodes.Status401Unauthorized,
        CompareShopAPI.Application.Admin.AdminForbiddenException => StatusCodes.Status403Forbidden,
        ArgumentException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
}
