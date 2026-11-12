using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using CompareShopAPI.Api.Auth;
using CompareShopAPI.Api.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using CompareShopAPI.Application.UserCommerce;
using CompareShopAPI.Domain.UserCommerce;
using Xunit;

namespace CompareShopAPI.Tests;
public sealed class ApiFactory(string environment = "Production", Dictionary<string,string?>? settings = null, bool signing = false, TimeProvider? timeProvider = null, Action<IServiceCollection>? configureServices = null, bool authSigning = false, IDocumentRetriever? authDocumentRetriever = null) : WebApplicationFactory<Program>
{
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-signing-key-at-least-32-bytes-long"));
    public static readonly RSA AuthRsa = RSA.Create(2048);
    private static readonly RSA OtherAuthRsa = RSA.Create(2048);
    internal static readonly StaticDocumentRetriever AuthJwksRetriever = new(CreateAuthJwks());
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings ?? new()));
        if (string.IsNullOrWhiteSpace(settings?.GetValueOrDefault("ConnectionStrings:CompareShopDb"))) builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IUserProvisioning>();
            services.AddSingleton<IUserProvisioning, InMemoryUserProvisioning>();
        });
        if (timeProvider is not null) builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(timeProvider);
        });
        if (configureServices is not null) builder.ConfigureTestServices(configureServices);
        if (signing) builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration { Issuer = "https://test-issuer" };
            options.Configuration.SigningKeys.Add(Key);
            options.TokenValidationParameters.ValidIssuer = "https://test-issuer";
            options.TokenValidationParameters.ValidAudience = "compareshop-api";
            options.TokenValidationParameters.IssuerSigningKey = Key;
        }));
        if (authSigning) builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>("CompareShopAuthAPI", options =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthAPI:Authority"] = "https://auth-test-issuer",
                ["AuthAPI:Issuer"] = "https://auth-test-issuer",
                ["AuthAPI:Audience"] = "compareshop-api"
            }).Build();
            AuthApiJwtBearerOptions.Configure(options, configuration, authDocumentRetriever ?? AuthJwksRetriever);
        }));
    }
    private static string CreateAuthJwks()
    {
        var parameters = AuthRsa.ExportParameters(false);
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = "auth-test-key",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!)
                }
            }
        });
    }
    public static string Token(string? roles = null, string issuer = "https://test-issuer", string audience = "compareshop-api", bool expired = false, string subject = "test-user", bool includeSubject = true)
    {
        var claims = new List<Claim>();
        if (includeSubject) claims.Add(new("sub", subject));
        if (roles != null) claims.Add(new("resource_access", roles));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
            expires: expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
    }

    public static string AuthToken(bool emailVerified = true, string issuer = "https://auth-test-issuer", string audience = "compareshop-api", bool expired = false, string subject = "auth-user")
    {
        var claims = new[] { new Claim("sub", subject), new Claim("email_verified", emailVerified ? "true" : "false") };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
            expires: expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(AuthRsa) { KeyId = "auth-test-key" }, SecurityAlgorithms.RsaSha256)));
    }

    public static string WrongSignatureAuthToken()
    {
        var claims = new[] { new Claim("sub", "auth-user"), new Claim("email_verified", "true") };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://auth-test-issuer", "compareshop-api", claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new RsaSecurityKey(OtherAuthRsa), SecurityAlgorithms.RsaSha256)));
    }
}

internal sealed class StaticDocumentRetriever(string document) : IDocumentRetriever
{
    public string Document { get; } = document;
    public int Requests { get; private set; }

    public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        Requests++;
        Assert.EndsWith("/api/v1/auth/jwks", address, StringComparison.Ordinal);
        return Task.FromResult(Document);
    }
}

internal sealed class InMemoryUserProvisioning : IUserProvisioning
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _users = new(StringComparer.Ordinal);
    private int _nextId;

    public Task<int> GetOrCreate(string keycloakSubject, CancellationToken ct) =>
        Task.FromResult(_users.GetOrAdd(keycloakSubject, _ => Interlocked.Increment(ref _nextId)));

    public Task<int> GetOrCreateForAuth(string authSubject, CancellationToken ct) =>
        Task.FromResult(_users.GetOrAdd($"auth:{authSubject}", _ => Interlocked.Increment(ref _nextId)));

    public Task<UserProvisioningResult> GetOrCreateForAdmin(string keycloakSubject, CancellationToken ct)
    {
        if (_users.TryGetValue(keycloakSubject, out var existing)) return Task.FromResult(new UserProvisioningResult(existing, false));
        var id = Interlocked.Increment(ref _nextId);
        return Task.FromResult(_users.TryAdd(keycloakSubject, id)
            ? new UserProvisioningResult(id, true)
            : new UserProvisioningResult(_users[keycloakSubject], false));
    }
}

public sealed class SkeletonTests
{
    [Fact]
    public async Task AuthAPI_JWKS_without_signing_keys_is_rejected()
    {
        var retriever = new StaticDocumentRetriever("{\"keys\":[]}");
        var configurationRetriever = new AuthApiJwksConfigurationRetriever("https://auth-test-issuer");
        await Assert.ThrowsAsync<InvalidOperationException>(() => configurationRetriever.GetConfigurationAsync(
            "https://auth-test-issuer/api/v1/auth/jwks", retriever, CancellationToken.None));
        Assert.Equal(1, retriever.Requests);
    }

    [Fact]
    public void AuthAPI_options_keep_authority_when_issuer_is_not_configured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuthAPI:Authority"] = "https://auth-test-issuer"
        }).Build();
        var options = new JwtBearerOptions();

        AuthApiJwtBearerOptions.Configure(options, configuration, new StaticDocumentRetriever("{}"));

        Assert.Equal("https://auth-test-issuer", options.Authority);
    }

    [Fact]
    public async Task AuthAPI_bearer_uses_production_registration_to_fetch_JWKS_over_HTTP_and_accept_valid_token()
    {
        var rsa = ApiFactory.AuthRsa.ExportParameters(false);
        var jwks = System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = "auth-test-key",
                    alg = SecurityAlgorithms.RsaSha256,
                    n = Base64UrlEncoder.Encode(rsa.Modulus!),
                    e = Base64UrlEncoder.Encode(rsa.Exponent!)
                }
            }
        });
        var jwksRequests = 0;
        using var jwksServer = new TestServer(new WebHostBuilder().Configure(app => app.Run(async context =>
        {
            Interlocked.Increment(ref jwksRequests);
            Assert.Equal("/api/v1/auth/jwks", context.Request.Path);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(jwks);
        })));

        await using var app = new ApiFactory(settings: new()
        {
            ["AuthAPI:Authority"] = "https://auth-test-issuer",
            ["AuthAPI:Issuer"] = "https://auth-test-issuer",
            ["AuthAPI:Audience"] = "compareshop-api"
        }, configureServices: services =>
        {
            services.AddHttpClient(AuthApiJwksOptionsPostConfigure.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(jwksServer.CreateHandler);
            services.AddSingleton<IUserCommerceRepository, EmptyUserCommerceRepository>();
        });
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.AuthToken());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/wishlist")).StatusCode);
        Assert.True(Volatile.Read(ref jwksRequests) > 0);
        var authOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JwtBearerOptions>>()
            .Get("CompareShopAuthAPI");
        Assert.IsType<ConfigurationManager<OpenIdConnectConfiguration>>(authOptions.ConfigurationManager);
    }

    [Fact]
    public async Task Public_commerce_requires_verified_RS256_AuthAPI_token_and_ignores_Keycloak_scheme()
    {
        var jwksRequests = 0;
        using var jwksServer = new TestServer(new WebHostBuilder().Configure(app => app.Run(async context =>
        {
            Interlocked.Increment(ref jwksRequests);
            Assert.Equal("/api/v1/auth/jwks", context.Request.Path);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(ApiFactory.AuthJwksRetriever.Document);
        })));
        var documentRetriever = new HttpDocumentRetriever(new HttpClient(jwksServer.CreateHandler())) { RequireHttps = true };
        await using var app = new ApiFactory(authSigning: true, configureServices: services =>
            services.AddSingleton<IUserCommerceRepository, EmptyUserCommerceRepository>(), authDocumentRetriever: documentRetriever);
        using var client = app.CreateClient();

        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.AuthToken());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/wishlist")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.AuthToken(emailVerified: false));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/wishlist")).StatusCode);

        foreach (var token in new[]
        {
            ApiFactory.AuthToken(issuer: "https://wrong-issuer"),
            ApiFactory.AuthToken(audience: "other-api"),
            ApiFactory.AuthToken(expired: true),
            ApiFactory.WrongSignatureAuthToken(),
            ApiFactory.Token()
        })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/wishlist")).StatusCode);
        }
        Assert.True(Volatile.Read(ref jwksRequests) > 0);
    }

    [Fact]
    public async Task Missing_AuthSubject_schema_returns_503_without_commerce_side_effects()
    {
        var repository = new CountingUserCommerceRepository();
        await using var app = new ApiFactory(authSigning: true, configureServices: services =>
        {
            services.RemoveAll<IUserProvisioning>();
            services.AddSingleton<IUserProvisioning, UnavailableUserProvisioning>();
            services.AddSingleton<IUserCommerceRepository>(repository);
        });
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.AuthToken());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/wishlist")).StatusCode);
        Assert.Equal(0, repository.WishlistReads);
    }

    [Fact]
    public async Task Missing_configuration_keeps_liveness_and_denies_admin()
    {
        await using var app = new ApiFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/articles")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }
    [Theory]
    [InlineData(null, 403)]
    [InlineData("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", 200)]
    [InlineData("{\"shortlink-admin-ui\":{\"roles\":[\"admin\"]}}", 403)]
    [InlineData("{\"compareshop-admin-ui\":{\"roles\":[\"reader\"]}}", 403)]
    [InlineData("invalid", 403)]
    [InlineData("[]", 403)]
    [InlineData("{\"compareshop-admin-ui\":null}", 403)]
    [InlineData("{\"compareshop-admin-ui\":{\"roles\":\"admin\"}}", 403)]
    [InlineData("{\"compareshop-admin-ui\":{\"roles\":[1,null,{}]}}", 403)]
    public async Task Real_signed_JWT_enforces_client_role(string? roles, int status)
    {
        await using var app = new ApiFactory(signing: true);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(roles));
        Assert.Equal(status, (int)(await client.GetAsync("/admin/health")).StatusCode);
    }
    [Theory]
    [InlineData("https://wrong-issuer", "compareshop-api", false)]
    [InlineData("https://test-issuer", "other", false)]
    [InlineData("https://test-issuer", "compareshop-api", true)]
    public async Task Invalid_issuer_audience_or_expiry_is_401(string issuer, string audience, bool expired)
    {
        await using var app = new ApiFactory(signing: true);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token(issuer: issuer, audience: audience, expired: expired));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/health")).StatusCode);
    }
    [Fact]
    public async Task Development_exposes_docs_and_exact_CORS_allowlist()
    {
        await using var app = new ApiFactory("Development", new() {
            ["Cors:AllowedOrigins:0"] = "https://compareshop-dev.giamha.online",
            ["Cors:AllowedOrigins:1"] = "https://admin-compareshop-dev.giamha.online"
        });
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/docs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        foreach(var origin in new[] {"https://compareshop-dev.giamha.online", "https://admin-compareshop-dev.giamha.online", "https://evil.example"})
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/admin/health");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "authorization");
            var response = await client.SendAsync(request);
            Assert.Equal(origin != "https://evil.example", response.Headers.Contains("Access-Control-Allow-Origin"));
            if (origin != "https://evil.example") Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
    }
    [Fact]
    public async Task Empty_CORS_configuration_denies_origin()
    {
        await using var app = new ApiFactory();
        var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/health");
        request.Headers.Add("Origin", "https://compareshop-dev.giamha.online");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        Assert.False((await client.SendAsync(request)).Headers.Contains("Access-Control-Allow-Origin"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unreachable_DB_and_failed_migration_preserve_host(bool migrate)
    {
        await using var app = new ApiFactory(settings: new() {
            ["ConnectionStrings:CompareShopDb"] = "Server=127.0.0.1,1;Database=CompareShop;User Id=sa;Password=test;Connect Timeout=1;ConnectRetryCount=0;Encrypt=False",
            ["Database:ApplyMigrations"] = migrate.ToString()
        });
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("Password", await response.Content.ReadAsStringAsync());
        Assert.Equal(migrate, app.Services.GetRequiredService<MigrationState>().Failed);
    }
    [Fact]
    public async Task Configured_authority_and_audience_validate_signed_admin_token()
    {
        await using var app = new ApiFactory(settings: new() {
            ["Keycloak:Authority"] = "https://test-issuer",
            ["Keycloak:Audience"] = "compareshop-api",
            ["OpenTelemetry:ServiceName"] = "compareshop-test",
            ["OpenTelemetry:OtlpEndpoint"] = "http://localhost:4317"
        }, signing: true);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/health")).StatusCode);
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        Assert.Equal("https://test-issuer", options.Authority);
        Assert.Equal("compareshop-api", options.Audience);
    }
    [Fact]
    public async Task Malformed_connection_string_is_unready_without_response_leaks()
    {
        await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = "invalid-connection" });
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
}

internal class EmptyUserCommerceRepository : IUserCommerceRepository
{
    public Task<bool> IsActiveProduct(int productId, CancellationToken ct) => Task.FromResult(true);
    public virtual Task<IReadOnlyList<WishlistItemDto>> Wishlist(int userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<WishlistItemDto>>([]);
    public Task AddWishlist(int userId, int productId, DateTime nowUtc, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveWishlist(int userId, int productId, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<PriceAlertDto>> PriceAlerts(int userId, CancellationToken ct) => Task.FromResult<IReadOnlyList<PriceAlertDto>>([]);
    public Task<PriceAlertDto> AddPriceAlert(int userId, int productId, int targetPrice, DateTime nowUtc, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> DeletePriceAlert(int userId, int alertId, CancellationToken ct) => Task.FromResult(true);
}

internal sealed class UnavailableUserProvisioning : IUserProvisioning
{
    public Task<int> GetOrCreate(string keycloakSubject, CancellationToken ct) => throw new InvalidOperationException();
    public Task<int> GetOrCreateForAuth(string authSubject, CancellationToken ct) => throw new AuthSubjectUnavailableException();
    public Task<UserProvisioningResult> GetOrCreateForAdmin(string keycloakSubject, CancellationToken ct) => throw new InvalidOperationException();
}

internal sealed class CountingUserCommerceRepository : EmptyUserCommerceRepository
{
    public int WishlistReads { get; private set; }
    public override Task<IReadOnlyList<WishlistItemDto>> Wishlist(int userId, CancellationToken ct)
    {
        WishlistReads++;
        return Task.FromResult<IReadOnlyList<WishlistItemDto>>([]);
    }
}
