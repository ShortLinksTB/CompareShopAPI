using System.Security.Claims;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Articles;
using CompareShopAPI.Domain.Articles;

namespace CompareShopAPI.Api.Articles;

public static class ArticleEndpoints
{
    public static RouteGroupBuilder MapArticleEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/articles", (int? page, int? pageSize, ArticleService service, CancellationToken ct) => Run(() => service.Published(page ?? 1, pageSize ?? 20, ct)));
        api.MapGet("/articles/{slug}", async (string slug, ArticleService service, CancellationToken ct) =>
        {
            try { return await service.PublishedBySlug(slug, ct) is { } article ? Results.Ok(article) : Results.NotFound(); }
            catch (ArgumentException) { return Results.NotFound(); }
        });
        return api;
    }

    public static RouteGroupBuilder MapAdminArticleEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/articles", (HttpContext c, int? page, int? pageSize, ArticleService s, CancellationToken ct) => Run(() => s.AdminList(Subject(c), page ?? 1, pageSize ?? 20, ct)));
        admin.MapGet("/articles/{id:int}", async (HttpContext c, int id, ArticleService s, CancellationToken ct) =>
        {
            try { return await s.AdminGet(Subject(c), id, ct) is { } article ? Results.Ok(article) : Results.NotFound(); }
            catch (Exception ex) { return Error(ex); }
        });
        admin.MapPost("/articles", (HttpContext c, ArticleInput input, ArticleService s, CancellationToken ct) => Run(() => s.Create(Subject(c), input, ct), StatusCodes.Status201Created));
        admin.MapPut("/articles/{id:int}", (HttpContext c, int id, ArticleInput input, ArticleService s, CancellationToken ct) => Run(() => s.Update(Subject(c), id, input, ct)));
        admin.MapDelete("/articles/{id:int}", async (HttpContext c, int id, ArticleService s, CancellationToken ct) =>
        {
            try { await s.Archive(Subject(c), id, ct); return Results.NoContent(); }
            catch (Exception ex) { return Error(ex); }
        });
        admin.MapPost("/articles/{id:int}/transitions/{target}", (HttpContext c, int id, string target, ArticleTransitionInput input, ArticleService s, CancellationToken ct) => Run(() =>
        {
            if (!target.Equals(input.Target, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The route target and request target must match.");
            if (target.Equals("scheduled", StringComparison.OrdinalIgnoreCase) && input.ScheduledAtUtc is null) throw new ArgumentException("scheduledAtUtc is required when scheduling.");
            return s.Transition(Subject(c), id, target, input.ScheduledAtUtc, ct);
        }));
        return admin;
    }

    private static string? Subject(HttpContext context) => context.User.FindFirst("sub")?.Value;
    private static async Task<IResult> Run<T>(Func<Task<T>> operation, int status = StatusCodes.Status200OK)
    {
        try { var value = await operation(); return status == StatusCodes.Status201Created ? Results.Json(value, statusCode: status) : Results.Ok(value); }
        catch (Exception ex) { return Error(ex); }
    }
    private static IResult Error(Exception ex) => ex switch
    {
        AdminUnauthorizedException => Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized),
        AdminForbiddenException => Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden),
        KeyNotFoundException => Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound),
        ArticleSlugConflictException => Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict),
        InvalidArticleTransitionException => Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict),
        InvalidOperationException => Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict),
        ArgumentException => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }),
        _ => Results.Problem("An unexpected error occurred.", statusCode: StatusCodes.Status500InternalServerError)
    };
}
