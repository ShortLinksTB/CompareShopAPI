using CompareShopAPI.Application.UserCommerce;

namespace CompareShopAPI.Api.Admin;

public sealed class AdminIdentityProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AdminIdentityProvisioningService provisioning)
    {
        if (context.User.Identity?.IsAuthenticated == true && IsAdminRoute(context.Request.Path))
        {
            var subject = context.User.FindFirst("sub")?.Value;
            if (!string.IsNullOrWhiteSpace(subject))
                await provisioning.Provision(subject, context.RequestAborted);
        }

        await next(context);
    }

    private static bool IsAdminRoute(PathString path) =>
        path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/v1/admin", StringComparison.OrdinalIgnoreCase);
}
