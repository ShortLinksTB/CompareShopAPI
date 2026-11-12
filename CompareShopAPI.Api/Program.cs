using CompareShopAPI.Api.Auth;
using CompareShopAPI.Api.Catalog;
using CompareShopAPI.Api.Admin;
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
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Infrastructure.Admin;
using CompareShopAPI.Api.Articles;
using CompareShopAPI.Application.Articles;
using CompareShopAPI.Infrastructure.Articles;
using CompareShopAPI.Infrastructure.Ingestion;
using CompareShopAPI.Api.Files;
using CompareShopAPI.Application.Files;
using CompareShopAPI.Infrastructure.Files;

var seedCatalogRequested = CatalogSeedCommand.IsRequested(args);
var matchingFixtureRequested = MatchingFixtureCommand.IsRequested(args);
var builder = WebApplication.CreateBuilder(seedCatalogRequested || matchingFixtureRequested ? Array.Empty<string>() : args);
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
builder.Services.AddScoped<AdminIdentityProvisioningService>();
builder.Services.AddScoped<IUserCommerceRepository, EfUserCommerceRepository>();
builder.Services.AddScoped<WishlistService>();
builder.Services.AddScoped<PriceAlertService>();
builder.Services.AddScoped<PriceHistoryService>();
builder.Services.AddScoped<IAdminData, EfAdminData>();
builder.Services.AddScoped<IBannedItemData, EfBannedItemData>();
builder.Services.AddScoped<BannedItemService>();
builder.Services.AddScoped<IArticleRepository, EfArticleRepository>();
builder.Services.AddScoped<IBlogArticleRepository, EfArticleRepository>();
builder.Services.AddScoped<IArticlePermissionGate, AdminArticlePermissionGate>();
builder.Services.AddScoped<IArticleProductLookup, UserCommerceArticleProductLookup>();
builder.Services.AddScoped<ArticleService>();
builder.Services.AddHostedService<ArticlePublishingWorker>();
builder.Services.AddScoped<BlogArticleService>();
builder.Services.AddScoped<ISyncJobStore, EfSyncJobStore>();
builder.Services.AddScoped<IDataSourceStore, EfDataSourceStore>();
builder.Services.AddScoped<IPriceHistoryRecorder, EfPriceHistoryRecorder>();
builder.Services.AddScoped<IAdminPermissionReader>(services => services.GetRequiredService<IAdminData>() as IAdminPermissionReader
    ?? throw new InvalidOperationException("Admin permission reader is unavailable."));
builder.Services.AddScoped<IngestionAdminService>();
builder.Services.AddScoped<DataSourceAdminService>();
builder.Services.AddScoped<AdminService>(services => new AdminService(
    services.GetRequiredService<IAdminData>(),
    services.GetRequiredService<IConfiguration>().GetSection("Admin:BootstrapSuperAdminSubjects").GetChildren()
        .Select(section => section.Value).Where(subject => !string.IsNullOrWhiteSpace(subject)).Cast<string>().ToHashSet(StringComparer.Ordinal)));
builder.Services.AddScoped<IFileRepository, EfFileRepository>();
builder.Services.AddScoped<IFileObjectStorage, MinioFileObjectStorage>();
builder.Services.AddScoped<IFilePermissionGate, AdminFilePermissionGate>();
builder.Services.AddScoped<FileService>();
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
}).AddJwtBearer(AuthApiAuthenticationDefaults.Scheme, options =>
{
    var authority = builder.Configuration["AuthAPI:Authority"];
    var issuer = builder.Configuration["AuthAPI:Issuer"];
    var audience = builder.Configuration["AuthAPI:Audience"];
    options.MapInboundClaims = false;
    options.RequireHttpsMetadata = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = string.IsNullOrWhiteSpace(issuer) ? null : issuer,
        ValidateAudience = true,
        ValidAudience = string.IsNullOrWhiteSpace(audience) ? null : audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        NameClaimType = "sub"
    };
    if (!string.IsNullOrWhiteSpace(authority)) options.Authority = authority;
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Admin", policy => policy.RequireAuthenticatedUser().AddRequirements(new AdminClientRoleRequirement()));
    options.AddPolicy("AuthenticatedUser", policy => policy.AddAuthenticationSchemes(AuthApiAuthenticationDefaults.Scheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.FindFirst("sub")?.Value)
            && string.Equals(context.User.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase)));
});
builder.Services.AddSingleton<IAuthorizationHandler, AdminClientRoleHandler>();
builder.Services.AddOpenApi();
builder.Services.AddCompareShopTelemetry(builder.Configuration);
var app = builder.Build();
if (await CatalogSeedCommand.TryRunAsync(args, app.Environment, app.Configuration, app.Services, app.Logger)) return;
if (await MatchingFixtureCommand.TryRunAsync(args, app.Environment, app.Configuration, app.Services)) return;
app.UseCors("UiClients");
app.UseAuthentication();
app.UseMiddleware<AdminIdentityProvisioningMiddleware>();
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapGroup("/admin").RequireAuthorization("Admin").MapAdminEndpoints();
app.MapGroup("/api/v1").MapCatalogEndpoints();
app.MapGroup("/api/v1").MapUserCommerceEndpoints();
app.MapGroup("/api/v1").MapArticleBlogEndpoints();
app.MapGroup("/admin").RequireAuthorization("Admin").MapAdminArticleBlogEndpoints();
app.MapGroup("/admin").RequireAuthorization("Admin").MapAdminFileEndpoints();
app.MapGroup("/files").AllowAnonymous().MapPublicFileEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs");
}
await app.ApplyMigrationsAsync();
app.Run();
public partial class Program;
