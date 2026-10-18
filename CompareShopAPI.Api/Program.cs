using CompareShopAPI.Api.Auth;
using CompareShopAPI.Api.Catalog;
using CompareShopAPI.Api.Health;
using CompareShopAPI.Api.Observability;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Api.UserCommerce;
using CompareShopAPI.Application.UserCommerce;
using CompareShopAPI.Infrastructure.UserCommerce;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using CompareShopAPI.Api.RateLimiting;

var seedCatalogRequested = CatalogSeedCommand.IsRequested(args);
var builder = WebApplication.CreateBuilder(seedCatalogRequested ? Array.Empty<string>() : args);
builder.Services.AddDbContext<CompareShopDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("CompareShopDb") ?? "", sql => sql.CommandTimeout(5)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICatalogQueries, EfCatalogQueries>();
builder.Services.AddScoped<IPriceHistoryStore, EfPriceHistoryStore>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<IAffiliateRedirectRepository, EfAffiliateRedirectRepository>();
builder.Services.AddScoped<AffiliateRedirectService>();
builder.Services.AddSingleton<IClientIpHasher, HmacClientIpHasher>();
builder.Services.Configure<AffiliateRedirectRateLimitOptions>(builder.Configuration.GetSection(AffiliateRedirectRateLimitOptions.SectionName));
builder.Services.AddSingleton<IClientIpResolver, ClientIpResolver>();
builder.Services.AddSingleton<IAffiliateRedirectRateLimiter>(services => new RedisAffiliateRedirectRateLimiter(
    builder.Configuration["Redis:ConnectionString"],
    builder.Configuration["Redis:KeyPrefix"],
    services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AffiliateRedirectRateLimitOptions>>(),
    services.GetRequiredService<ILogger<RedisAffiliateRedirectRateLimiter>>()));
builder.Services.AddScoped<IUserProvisioning, EfUserProvisioning>();
builder.Services.AddScoped<IUserCommerceRepository, EfUserCommerceRepository>();
builder.Services.AddScoped<WishlistService>();
builder.Services.AddScoped<PriceAlertService>();
builder.Services.AddScoped<PriceHistoryService>();
builder.Services.AddSingleton<MigrationState>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealth>("database", tags: ["ready"]);
builder.Services.AddCors(options => options.AddPolicy("UiClients", policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0) policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    var authority = builder.Configuration["Keycloak:Authority"];
    var audience = builder.Configuration["Keycloak:Audience"];
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true
    };
    if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(audience))
    {
        options.Authority = authority;
        options.Audience = audience;
    }
    // With no issuer/audience/signing keys configured all bearer tokens fail closed.
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireAuthenticatedUser().AddRequirements(new AdminClientRoleRequirement()));
    options.AddPolicy("AuthenticatedUser", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.FindFirst("sub")?.Value)));
});
builder.Services.AddSingleton<IAuthorizationHandler, AdminClientRoleHandler>();
builder.Services.AddOpenApi();
builder.Services.AddCompareShopTelemetry(builder.Configuration);
var app = builder.Build();
if (await CatalogSeedCommand.TryRunAsync(args, app.Environment, app.Configuration, app.Services, app.Logger)) return;
app.UseCors("UiClients");
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapGroup("/admin").RequireAuthorization("Admin").MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGroup("/api/v1").MapCatalogEndpoints();
app.MapGroup("/api/v1").MapUserCommerceEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs");
}
await app.ApplyMigrationsAsync();
app.Run();
public partial class Program;
