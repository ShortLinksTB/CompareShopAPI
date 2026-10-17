using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
namespace CompareShopAPI.Api.Auth;
public sealed record AdminClientRoleRequirement : IAuthorizationRequirement;
public sealed class AdminClientRoleHandler : AuthorizationHandler<AdminClientRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminClientRoleRequirement requirement)
    {
        var json = context.User.FindFirst("resource_access")?.Value;
        if (json is null) return Task.CompletedTask;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("compareshop-admin-ui", out var client)
                && client.ValueKind == JsonValueKind.Object
                && client.TryGetProperty("roles", out var roles)
                && roles.ValueKind == JsonValueKind.Array
                && roles.EnumerateArray().Any(role => role.ValueKind == JsonValueKind.String && role.GetString() == "admin"))
                context.Succeed(requirement);
        }
        catch (JsonException) { /* Invalid claims must deny authorization. */ }
        return Task.CompletedTask;
    }
}
