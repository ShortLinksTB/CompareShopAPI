using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using CompareShopAPI.Api.Auth;
using CompareShopAPI.Api.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Xunit;

namespace CompareShopAPI.Tests;
public sealed class ApiFactory(string environment = "Production", Dictionary<string,string?>? settings = null, bool signing = false, TimeProvider? timeProvider = null) : WebApplicationFactory<Program>
{
    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-signing-key-at-least-32-bytes-long"));
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings ?? new()));
        if (timeProvider is not null) builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(timeProvider);
        });
        if (signing) builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration { Issuer = "https://test-issuer" };
            options.Configuration.SigningKeys.Add(Key);
            options.TokenValidationParameters.ValidIssuer = "https://test-issuer";
            options.TokenValidationParameters.ValidAudience = "compareshop-api";
            options.TokenValidationParameters.IssuerSigningKey = Key;
        }));
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
}
public sealed class SkeletonTests
{
    [Fact]
    public async Task Missing_configuration_keeps_liveness_and_denies_admin()
    {
        await using var app = new ApiFactory();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/health")).StatusCode);
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
    [Fact]
    public async Task SqlServer_readiness_uses_real_database()
    {
        var connection = Environment.GetEnvironmentVariable("COMPARESHOP_SQLSERVER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connection), "MSSQL test connection is required; this test must not silently skip.");
        await using var app = new ApiFactory(settings: new() { ["ConnectionStrings:CompareShopDb"] = connection });
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        app.Services.GetRequiredService<MigrationState>().Failed = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
