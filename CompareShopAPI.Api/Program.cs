using CompareShopAPI.Api.Auth;
using CompareShopAPI.Api.Catalog;
using CompareShopAPI.Api.Health;
using CompareShopAPI.Api.Observability;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.Catalog;
using CompareShopAPI.Application.Catalog;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<CompareShopDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("CompareShopDb") ?? "", sql => sql.CommandTimeout(5)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ICatalogQueries, EfCatalogQueries>();
builder.Services.AddScoped<CatalogService>();
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
builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy =>
    policy.RequireAuthenticatedUser().AddRequirements(new AdminClientRoleRequirement())));
builder.Services.AddSingleton<IAuthorizationHandler, AdminClientRoleHandler>();
builder.Services.AddOpenApi();
builder.Services.AddCompareShopTelemetry(builder.Configuration);
var app = builder.Build();
app.UseCors("UiClients");
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapGroup("/admin").RequireAuthorization("Admin").MapGet("/health", () => Results.Ok(new { status = "ok" }));
if (builder.Configuration.GetValue<bool>("CatalogSeed:Enabled") &&
    string.Equals(builder.Configuration["CatalogSeed:Environment"], "dev", StringComparison.Ordinal))
{
    app.MapPost("/admin/dev/catalog-seed", async (CompareShopDbContext db, CancellationToken cancellationToken) =>
    {
        await CatalogSeed.SeedAsync(db, cancellationToken);
        return Results.NoContent();
    }).RequireAuthorization("Admin");
}
app.MapGroup("/api/v1").MapCatalogEndpoints();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference("/docs");
}
await app.ApplyMigrationsAsync();
app.Run();
public partial class Program;
