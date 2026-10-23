using CompareShopAPI.Application.UserCommerce;
using Microsoft.AspNetCore.Authorization;

namespace CompareShopAPI.Api.Admin;

public sealed class AdminIdentityProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AdminIdentityProvisioningService provisioning)
    {
        if (context.User.Identity?.IsAuthenticated == true && IsAdminEndpoint(context.GetEndpoint()))
        {
            var subject = context.User.FindFirst("sub")?.Value;
            if (!string.IsNullOrWhiteSpace(subject))
                await provisioning.Provision(subject, context.RequestAborted);
        }

        await next(context);
    }

    private static bool IsAdminEndpoint(Endpoint? endpoint) => endpoint is not null
        && endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => string.Equals(metadata.Policy, "Admin", StringComparison.Ordinal));
}
