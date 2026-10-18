using System.Net;
using System.Security.Cryptography;
using System.Text;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Api.RateLimiting;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class AffiliateRedirectTests
{
    [Fact]
    public async Task Public_redirect_uses_stored_destination_records_click_and_is_rate_limited()
    {
        var repository = new FakeAffiliateRedirectRepository();
        await using var app = new ApiFactory(configureServices: services => ConfigureAffiliateServices(services, repository, useRealRateLimiter: true));
        using var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        for (var i = 0; i < 30; i++)
        {
            using var response = await client.GetAsync("/api/v1/go/17?url=https%3A%2F%2Fevil.example");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("https://affiliate-test.example/offer/17", response.Headers.Location?.AbsoluteUri);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        }

        using var limited = await client.GetAsync("/api/v1/go/17");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single()) > 0);
        Assert.Equal(30, repository.RecordedClicks);
    }

    [Theory]
    [InlineData(true, false, "https://partner.example/path", false)]
    [InlineData(false, false, "https://partner.example/path", true)]
    [InlineData(true, true, "https://partner.example/path", true)]
    [InlineData(true, false, "http://partner.example/path", true)]
    [InlineData(true, false, "//partner.example/path", true)]
    [InlineData(true, false, "https://partner.example/path\nheader", true)]
    public void Link_rejects_disabled_stale_or_invalid_destination(bool enabled, bool stale, string url, bool rejected)
    {
        var link = new AffiliateLink { Id = 1, ProductOfferId = 2, DestinationUrl = url, IsEnabled = enabled, IsStale = stale };
        Assert.Equal(!rejected, link.TryGetRedirectDestination(out var destination));
        Assert.Equal(rejected ? null : new Uri(url), destination);
    }

    [Fact]
    public async Task Tracking_failure_is_reported_without_redirect()
    {
        var repository = new FakeAffiliateRedirectRepository { PersistClicks = false };
        var service = new AffiliateRedirectService(repository, new FakeIpHasher(), TimeProvider.System);
        var result = await service.Redirect(17, "192.0.2.4", default);
        Assert.True(result.TrackingFailed);
        Assert.False(result.Found);
        Assert.Null(result.Destination);
    }

    [Fact]
    public async Task Tracking_failure_returns_503_without_location()
    {
        var repository = new FakeAffiliateRedirectRepository { PersistClicks = false };
        await using var app = new ApiFactory(configureServices: services => ConfigureAffiliateServices(services, repository));
        using var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/v1/go/17");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("stale")]
    [InlineData("invalid-url")]
    [InlineData("missing")]
    public async Task Missing_disabled_stale_or_invalid_link_returns_safe_404(string state)
    {
        var repository = new FakeAffiliateRedirectRepository();
        switch (state)
        {
            case "disabled": repository.Link.IsEnabled = false; break;
            case "stale": repository.Link.IsStale = true; break;
            case "invalid-url": repository.Link.DestinationUrl = "http://unsafe.example"; break;
        }
        await using var app = new ApiFactory(configureServices: services => ConfigureAffiliateServices(services, repository));
        using var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(state == "missing" ? "/api/v1/go/18" : "/api/v1/go/17");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(0, repository.RecordedClicks);
        Assert.DoesNotContain("affiliate", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Client_ip_is_stored_as_a_keyed_hmac_digest_or_omitted_without_a_key()
    {
        const string key = "test-hmac-secret-key";
        const string ip = "192.0.2.4";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AffiliateTracking:IpHashKey"] = key }).Build();
        var hash = new HmacClientIpHasher(configuration).Hash(ip);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("192.0.2.4")));
        Assert.Equal(expected, hash);
        Assert.Equal(64, hash?.Length);
        Assert.Null(new HmacClientIpHasher(new ConfigurationBuilder().Build()).Hash(ip));
        Assert.Null(new HmacClientIpHasher(configuration).Hash("not-an-ip"));
    }

    [Fact]
    public void Client_ip_resolver_ignores_untrusted_forwarding_and_walks_trusted_proxy_chain()
    {
        var options = Options.Create(new AffiliateRedirectRateLimitOptions { TrustedProxies = ["10.0.0.2", "10.0.0.3"] });
        var resolver = new ClientIpResolver(options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.11";
        Assert.Equal("192.0.2.10", resolver.Resolve(context));
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.2");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.11, 10.0.0.3";
        Assert.Equal("198.51.100.11", resolver.Resolve(context));
        context.Connection.RemoteIpAddress = null;
        Assert.Null(resolver.Resolve(context));
    }

    [Fact]
    public async Task Missing_redis_configuration_uses_per_ip_fallback_and_returns_retry_after()
    {
        var limiter = new RedisAffiliateRedirectRateLimiter(null, "compareshop:", Options.Create(new AffiliateRedirectRateLimitOptions
        {
            PermitLimit = 30,
            WindowSeconds = 60,
            GraceSeconds = 5
        }), NullLogger<RedisAffiliateRedirectRateLimiter>.Instance);
        using (limiter)
        {
            for (var i = 0; i < 30; i++)
                Assert.Equal(AffiliateRedirectRateLimitStatus.Allowed, (await limiter.TryAcquireAsync("192.0.2.4", default)).Status);

            var limited = await limiter.TryAcquireAsync("192.0.2.4", default);
            Assert.Equal(AffiliateRedirectRateLimitStatus.Rejected, limited.Status);
            Assert.InRange(limited.RetryAfterSeconds, 1, 60);
            Assert.Equal(AffiliateRedirectRateLimitStatus.Allowed, (await limiter.TryAcquireAsync("192.0.2.5", default)).Status);
        }
    }

    [Fact]
    public async Task Click_count_and_event_are_saved_together_without_raw_ip()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
        int linkId;
        await using (var db = new CompareShopDbContext(options))
        {
            var category = new Category { Id = 1, Slug = "test", Name = "Test", IsActive = true };
            var shop = new PartnerShop { Id = 1, Slug = "test", Name = "Test", IsActive = true };
            var product = new Product { Id = 1, Slug = "test", Name = "Test", Category = category, IsActive = true };
            var offer = new ProductOffer { Id = 17, Product = product, PartnerShop = shop, PriceVnd = 100, UpdatedAtUtc = now, IsActive = true };
            var link = new AffiliateLink { ProductOffer = offer, DestinationUrl = "https://partner.example/path", IsEnabled = true, IsStale = false };
            db.AffiliateLinks.Add(link);
            await db.SaveChangesAsync();
            linkId = link.Id;
        }

        await using (var db = new CompareShopDbContext(options))
        {
            var repository = new EfAffiliateRedirectRepository(db, NullLogger<EfAffiliateRedirectRepository>.Instance);
            Assert.NotNull(await repository.FindRedirectableLink(17, default));
            Assert.Null(await repository.FindRedirectableLink(18, default));
            var offer = await db.ProductOffers.SingleAsync();
            offer.IsActive = false;
            await db.SaveChangesAsync();
            Assert.Null(await repository.FindRedirectableLink(17, default));
            offer.IsActive = true;
            await db.SaveChangesAsync();
            Assert.True(await repository.RecordClick(linkId, now, "0123456789abcdef", default));
        }
        await using (var db = new CompareShopDbContext(options))
        {
            var link = await db.AffiliateLinks.SingleAsync();
            var click = await db.AffiliateClicks.SingleAsync();
            Assert.Equal(1, link.ClickCount);
            Assert.Equal(now, click.ClickedAtUtc);
            Assert.Equal("0123456789abcdef", click.ClientIpDigest);
            Assert.DoesNotContain("192.0.2.4", click.ClientIpDigest);
        }
    }

    private sealed class FakeAffiliateRedirectRepository : IAffiliateRedirectRepository
    {
        private readonly AffiliateLink link = new() { Id = 3, ProductOfferId = 17, DestinationUrl = "https://affiliate-test.example/offer/17", IsEnabled = true, IsStale = false };
        public AffiliateLink Link => link;
        public int RecordedClicks { get; private set; }
        public bool PersistClicks { get; set; } = true;
        public Task<AffiliateLink?> FindRedirectableLink(int offerId, CancellationToken ct) => Task.FromResult(offerId == 17 ? link : null);
        public Task<bool> RecordClick(int affiliateLinkId, DateTime clickedAtUtc, string? clientIpDigest, CancellationToken ct)
        {
            if (PersistClicks) RecordedClicks++;
            return Task.FromResult(PersistClicks);
        }
    }

    private sealed class FakeIpHasher : IClientIpHasher
    {
        public string? Hash(string? clientIp) => clientIp is null ? null : "test-keyed-digest";
    }

    private sealed class FakeClientIpResolver : IClientIpResolver
    {
        public string? Resolve(HttpContext context) => "192.0.2.4";
    }

    private sealed class FakeAffiliateRedirectRateLimiter : IAffiliateRedirectRateLimiter
    {
        private int requests;
        public Task<AffiliateRedirectRateLimitDecision> TryAcquireAsync(string canonicalClientIp, CancellationToken ct) =>
            Task.FromResult(Interlocked.Increment(ref requests) <= 30
                ? AffiliateRedirectRateLimitDecision.Allow()
                : AffiliateRedirectRateLimitDecision.Reject(60));
    }

    private static void ConfigureAffiliateServices(IServiceCollection services, FakeAffiliateRedirectRepository repository, bool useRealRateLimiter = false)
    {
        services.RemoveAll<IAffiliateRedirectRepository>();
        services.AddSingleton<IAffiliateRedirectRepository>(repository);
        services.RemoveAll<IAffiliateRedirectRateLimiter>();
        if (useRealRateLimiter)
        {
            services.AddSingleton<IAffiliateRedirectRateLimiter>(new RedisAffiliateRedirectRateLimiter(
                null,
                "compareshop:",
                Options.Create(new AffiliateRedirectRateLimitOptions { PermitLimit = 30, WindowSeconds = 60, GraceSeconds = 5 }),
                NullLogger<RedisAffiliateRedirectRateLimiter>.Instance));
        }
        else services.AddSingleton<IAffiliateRedirectRateLimiter>(new FakeAffiliateRedirectRateLimiter());
        services.RemoveAll<IClientIpResolver>();
        services.AddSingleton<IClientIpResolver>(new FakeClientIpResolver());
    }
}
