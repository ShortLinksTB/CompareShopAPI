using CompareShopAPI.Api.Admin;
using CompareShopAPI.Application.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class CategoryImportRouteMappingTests
{
    [Fact]
    public async Task Contract_routes_are_mapped_under_api_admin_with_admin_authorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<CategoryImportService>();
        await using var app = builder.Build();

        app.MapGroup("/admin").RequireAuthorization("Admin").MapCategoryImportEndpoints();
        app.MapGroup("/api/admin").RequireAuthorization("Admin").MapCategoryImportEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        foreach (var prefix in new[] { "/admin", "/api/admin" })
        {
            foreach (var (path, method) in new[]
            {
                ($"{prefix}/categories/export", "GET"),
                ($"{prefix}/categories/import/dry-run", "POST"),
                ($"{prefix}/categories/import/commit", "POST")
            })
            {
                var endpoint = Assert.IsType<RouteEndpoint>(endpoints
                    .SingleOrDefault(candidate => candidate is RouteEndpoint route
                        && route.RoutePattern.RawText == path
                        && route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true));

                Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), authorization => authorization.Policy == "Admin");
            }
        }

        Assert.Equal(6, endpoints.Length);
    }
}
