using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using CompareShopAPI.Api.HomeShowcase;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Application.HomeShowcase;
using CompareShopAPI.Domain.HomeShowcase;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class HomeShowcaseEndpointTests
{
    [Fact]
    public async Task Admin_and_public_routes_apply_permissions_cache_and_preview_response_headers()
    {
        var store = new EndpointStore();
        var permissions = new EndpointPermissions();
        await using var app = await CreateApp(store, permissions);
        var client = app.GetTestClient();

        var forbidden = await client.GetAsync("/admin/home-showcase");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        permissions.Grant(AdminPermissions.HomeShowcaseRead, AdminPermissions.HomeShowcaseUpdate);

        var admin = await client.GetAsync("/admin/home-showcase");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Equal("no-store", admin.Headers.CacheControl?.ToString());

        var duplicate = new HomeShowcaseDraftInput(
            Enumerable.Range(1, 15).Select(index => new HomeShowcaseDraftSlotInput(index, index <= 2 ? 1 : null)).ToArray(),
            Enumerable.Range(1, 15).Select(index => new HomeShowcaseDraftSlotInput(index, null)).ToArray());
        var invalid = await client.PutAsJsonAsync("/admin/home-showcase/draft", duplicate);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        permissions.Grant(AdminPermissions.HomeShowcaseUpdate);
        var valid = new HomeShowcaseDraftInput(
            Enumerable.Range(1, 15).Select(index => new HomeShowcaseDraftSlotInput(index, null)).ToArray(),
            Enumerable.Range(1, 15).Select(index => new HomeShowcaseDraftSlotInput(index, null)).ToArray());
        var accepted = await client.PutAsJsonAsync("/admin/home-showcase/draft", valid);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, store.DraftWrites);

        var invalidIndex = new HomeShowcaseDraftInput(
            [new HomeShowcaseDraftSlotInput(0, null), .. Enumerable.Range(2, 14).Select(index => new HomeShowcaseDraftSlotInput(index, null))],
            valid.InterestedComputers);
        var rejected = await client.PutAsJsonAsync("/admin/home-showcase/draft", invalidIndex);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(1, store.DraftWrites);

        var nullSlot = new HomeShowcaseDraftInput(
            [null!, .. valid.Featured!.Skip(1)],
            valid.InterestedComputers);
        var nullSlotResponse = await client.PutAsJsonAsync("/admin/home-showcase/draft", nullSlot);
        Assert.Equal(HttpStatusCode.BadRequest, nullSlotResponse.StatusCode);
        var validation = await nullSlotResponse.Content.ReadAsStringAsync();
        Assert.Contains("featured[1].slotIndex", validation);
        Assert.Equal(1, store.DraftWrites);

        var tokenResponse = await client.PostAsync("/admin/home-showcase/preview-token", content: null);
        Assert.Equal((HttpStatusCode)429, tokenResponse.StatusCode);

        var preview = await client.PostAsJsonAsync("/api/v1/home/showcase/preview", new HomeShowcasePreviewInput("invalid"));
        Assert.Equal(HttpStatusCode.Unauthorized, preview.StatusCode);
        Assert.Equal("no-store", preview.Headers.CacheControl?.ToString());
        Assert.Equal("noindex, nofollow", preview.Headers.GetValues("X-Robots-Tag").Single());

        var publicOne = await client.GetAsync("/api/v1/home/showcase");
        var publicTwo = await client.GetAsync("/api/v1/home/showcase");
        Assert.Equal(HttpStatusCode.OK, publicOne.StatusCode);
        Assert.Contains("max-age=3", publicOne.Headers.CacheControl?.ToString());
        Assert.Equal(2, store.PublicReads);
        Assert.Equal(2, store.FallbackReads);

        await Task.Delay(TimeSpan.FromMilliseconds(3200));
        var refreshed = await client.GetAsync("/api/v1/home/showcase");
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.Equal(4, store.PublicReads);
        Assert.Equal(4, store.FallbackReads);
    }

    private static async Task<WebApplication> CreateApp(EndpointStore store, EndpointPermissions permissions)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IHomeShowcaseStore>(store);
        builder.Services.AddSingleton<IHomeShowcaseAudit, EndpointAudit>();
        builder.Services.AddSingleton<IAdminPermissionReader>(permissions);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<HomeShowcaseService>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "operator")], "test"));
            await next();
        });
        app.MapGroup("/admin").MapHomeShowcaseEndpoints();
        app.MapGroup("/api/v1").MapPublicHomeShowcaseEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class EndpointPermissions : IAdminPermissionReader
    {
        private readonly HashSet<string> grants = new(StringComparer.Ordinal);
        public void Grant(params string[] permissions) { foreach (var permission in permissions) grants.Add(permission); }
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(grants.Contains(permission));
    }

    private sealed class EndpointAudit : IHomeShowcaseAudit
    {
        public Task<T> Execute<T>(string actor, string action, object? before, object? after, Func<CancellationToken, Task<T>> operation, CancellationToken ct) => operation(ct);
    }

    private sealed class EndpointStore : IHomeShowcaseStore
    {
        public int PublicReads { get; private set; }
        public int FallbackReads { get; private set; }
        public int DraftWrites { get; private set; }
        public Task<IReadOnlyList<HomeShowcaseSlot>> Slots(HomeShowcaseSnapshot snapshot, CancellationToken ct) => Task.FromResult<IReadOnlyList<HomeShowcaseSlot>>([]);
        public Task<IReadOnlyDictionary<int, HomeShowcaseProductData>> Products(IReadOnlyCollection<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, HomeShowcaseProductData>>(new Dictionary<int, HomeShowcaseProductData>());
        public Task<bool> IsComputersCategoryAvailable(CancellationToken ct) => Task.FromResult(false);
        public Task<bool> AreComputers(IReadOnlyCollection<int> productIds, CancellationToken ct) => Task.FromResult(false);
        public Task ReplaceSnapshot(HomeShowcaseSnapshot snapshot, IReadOnlyList<int?> featured, IReadOnlyList<int?> interested, CancellationToken ct) { if (snapshot == HomeShowcaseSnapshot.Draft) DraftWrites++; return Task.CompletedTask; }
        public Task<IReadOnlyList<HomeShowcaseProduct>> PublicProducts(HomeShowcaseSection section, CancellationToken ct) { PublicReads++; return Task.FromResult<IReadOnlyList<HomeShowcaseProduct>>([]); }
        public Task<IReadOnlyList<HomeShowcaseProduct>> LegacyFallback(HomeShowcaseSection section, CancellationToken ct) { FallbackReads++; return Task.FromResult<IReadOnlyList<HomeShowcaseProduct>>([]); }
        public Task<int> PreviewTokensCreatedBy(string actor, DateTime sinceUtc, CancellationToken ct) => Task.FromResult(5);
        public Task SavePreviewToken(HomeShowcasePreviewToken token, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> ConsumePreviewToken(string tokenHash, DateTime nowUtc, CancellationToken ct) => Task.FromResult(false);
    }
}
