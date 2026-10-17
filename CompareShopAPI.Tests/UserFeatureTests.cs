using CompareShopAPI.Application.Users;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Users;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.Users;
using CompareShopAPI.Infrastructure.Persistence.Migrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Claims;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class UserFeatureTests
{
    [Fact]
    public async Task Jit_provisions_each_subject_once_and_keeps_wishlist_and_alerts_isolated()
    {
        await using var db = CreateDatabase();
        var product = await AddProduct(db, "camera");
        var service = CreateService(db);

        Assert.Empty(await service.Wishlist("keycloak-sub-a", default));
        Assert.Empty(await service.Wishlist("keycloak-sub-b", default));
        Assert.Equal(2, await db.Users.CountAsync());
        Assert.Equal(UserFeatureError.ProductNotFound, (await service.ChangeWishlist("keycloak-sub-a", int.MaxValue, "add", default)).Error);
        Assert.True((await service.ChangeWishlist("keycloak-sub-a", product.Id, "add", default)).Succeeded);
        Assert.True((await service.ChangeWishlist("keycloak-sub-a", product.Id, "ADD", default)).Succeeded);
        Assert.Single(await service.Wishlist("keycloak-sub-a", default));
        Assert.Empty(await service.Wishlist("keycloak-sub-b", default));

        Assert.Equal(UserFeatureError.InvalidInput, (await service.CreatePriceAlert("keycloak-sub-a", product.Id, 0, default)).Error);
        var created = await service.CreatePriceAlert("keycloak-sub-a", product.Id, 99_000, default);
        Assert.True(created.Succeeded);
        Assert.Single(await service.PriceAlerts("keycloak-sub-a", default));
        Assert.Empty(await service.PriceAlerts("keycloak-sub-b", default));
        Assert.False((await service.DeletePriceAlert("keycloak-sub-b", created.Value!.Id, default)).Succeeded);
        Assert.Single(await service.PriceAlerts("keycloak-sub-a", default));
        Assert.True((await service.DeletePriceAlert("keycloak-sub-a", created.Value.Id, default)).Succeeded);
        Assert.Empty(await service.PriceAlerts("keycloak-sub-a", default));
    }

    [Fact]
    public async Task Jit_provisioning_recovers_when_another_request_wins_the_unique_subject_insert()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var connection = new ConcurrentUserInsertInterceptor(databaseName, "racing-subject");
        var options = new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseInMemoryDatabase(databaseName).AddInterceptors(connection).Options;
        await using var db = new CompareShopDbContext(options);

        var userId = await new EfUserFeatureRepository(db,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero)))
            .GetOrCreateUserId("racing-subject", default);

        Assert.True(connection.RaceSimulated);
        Assert.Equal("racing-subject", (await db.Users.SingleAsync(x => x.Id == userId)).ExternalSubject);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Wishlist_operations_are_idempotent_and_inactive_products_are_rejected()
    {
        await using var db = CreateDatabase();
        var active = await AddProduct(db, "active-product");
        var inactive = await AddProduct(db, "inactive-product", active: false);
        var service = CreateService(db);

        Assert.Equal(UserFeatureError.InvalidInput, (await service.ChangeWishlist("subject", active.Id, "update", default)).Error);
        Assert.Equal(UserFeatureError.InvalidInput, (await service.ChangeWishlist("subject", active.Id, "1", default)).Error);
        Assert.Equal(UserFeatureError.ProductNotFound, (await service.ChangeWishlist("subject", inactive.Id, "add", default)).Error);
        Assert.True((await service.ChangeWishlist("subject", active.Id, "add", default)).Succeeded);
        Assert.True((await service.ChangeWishlist("subject", active.Id, "remove", default)).Succeeded);
        Assert.True((await service.ChangeWishlist("subject", active.Id, "remove", default)).Succeeded);
        Assert.Empty(await service.Wishlist("subject", default));
        Assert.Equal(UserFeatureError.ProductNotFound, (await service.CreatePriceAlert("subject", inactive.Id, 1, default)).Error);
    }

    [Fact]
    public void Model_has_unique_subject_and_wishlist_keys_and_only_local_product_user_relations()
    {
        using var db = new CompareShopDbContext(new DbContextOptionsBuilder<CompareShopDbContext>()
            .UseSqlServer("Server=localhost;Database=ModelOnly;Trusted_Connection=True;TrustServerCertificate=True").Options);
        var model = db.Model;
        var user = model.FindEntityType(typeof(CompareShopAPI.Domain.Users.User))!;
        var wishlist = model.FindEntityType(typeof(CompareShopAPI.Domain.Users.WishlistItem))!;
        var alerts = model.FindEntityType(typeof(CompareShopAPI.Domain.Users.PriceAlert))!;

        Assert.Contains(user.GetIndexes(), index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual(["ExternalSubject"]));
        Assert.Contains(wishlist.GetIndexes(), index => index.IsUnique && index.Properties.Select(x => x.Name).SequenceEqual(["UserId", "ProductId"]));
        Assert.Contains(alerts.GetIndexes(), index => index.Properties.Select(x => x.Name).SequenceEqual(["UserId", "IsActive"]));
        Assert.Equal(new[] { "Products", "Users" }, wishlist.GetForeignKeys().Select(x => x.PrincipalEntityType.GetTableName()).Order().ToArray());
        Assert.Equal(new[] { "Products", "Users" }, alerts.GetForeignKeys().Select(x => x.PrincipalEntityType.GetTableName()).Order().ToArray());
        Assert.Equal(new[] { "PriceAlerts", "Users", "WishlistItems" }, model.GetEntityTypes().Select(x => x.GetTableName()).Where(x => x is "PriceAlerts" or "Users" or "WishlistItems").Order().ToArray());
    }

    [Fact]
    public void Migration_creates_only_the_three_scoped_tables_with_required_constraints()
    {
        var migration = new AddUserWishlistPriceAlerts();
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        typeof(AddUserWishlistPriceAlerts).GetMethod("Up", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);

        Assert.Equal(new[] { "PriceAlerts", "Users", "WishlistItems" },
            builder.Operations.OfType<CreateTableOperation>().Select(x => x.Name).Order().ToArray());
        Assert.Contains(builder.Operations.OfType<CreateIndexOperation>(), x => x.Table == "Users" && x.IsUnique && x.Columns.SequenceEqual(["ExternalSubject"]));
        Assert.Contains(builder.Operations.OfType<CreateIndexOperation>(), x => x.Table == "WishlistItems" && x.IsUnique && x.Columns.SequenceEqual(["UserId", "ProductId"]));
        Assert.Contains(builder.Operations.OfType<CreateTableOperation>().Single(x => x.Name == "WishlistItems").ForeignKeys!, x => x.PrincipalTable == "Products");
        Assert.Contains(builder.Operations.OfType<CreateTableOperation>().Single(x => x.Name == "PriceAlerts").ForeignKeys!, x => x.PrincipalTable == "Users");
    }

    [Fact]
    public async Task Regular_authenticated_user_policy_does_not_require_admin_role_or_accept_blank_subject()
    {
        await using var app = new ApiFactory(signing: true);
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        var regularUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "regular-keycloak-user")], "test"));
        var blankSubject = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", " ")], "test"));

        Assert.True((await authorization.AuthorizeAsync(regularUser, "AuthenticatedUser")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(regularUser, "Admin")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(blankSubject, "AuthenticatedUser")).Succeeded);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await app.CreateClient().GetAsync("/api/v1/wishlist")).StatusCode);
    }

    [Fact]
    public async Task Authenticated_routes_support_crud_and_hide_another_users_alerts()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        await using var app = new ApiFactory(signing: true, serviceOverrides: services =>
        {
            services.RemoveAll<CompareShopDbContext>();
            services.RemoveAll<DbContextOptions<CompareShopDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CompareShopDbContext>>();
            services.AddDbContext<CompareShopDbContext>(options => options.UseInMemoryDatabase(databaseName));
        });
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/wishlist")).StatusCode);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CompareShopDbContext>();
            await AddProduct(db, "api-product");
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(subject: "user-a"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/wishlist", new { productId = 1, operation = "add" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/wishlist", new { productId = 1, operation = "add" })).StatusCode);
        var wishlist = await client.GetFromJsonAsync<JsonElement>("/api/v1/wishlist");
        Assert.Equal(1, wishlist.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/wishlist", new { productId = 1, operation = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/wishlist", new { productId = 999, operation = "add" })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(subject: "user-b"));
        var otherWishlist = await client.GetFromJsonAsync<JsonElement>("/api/v1/wishlist");
        Assert.Empty(otherWishlist.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/wishlist", new { productId = 1, operation = "remove" })).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(subject: "user-a"));
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/v1/wishlist")).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/wishlist", new { productId = 1, operation = "remove" })).StatusCode);

        var invalidAlert = await client.PostAsJsonAsync("/api/v1/price-alerts", new { productId = 1, targetPrice = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidAlert.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/v1/price-alerts", new { productId = 999, targetPrice = 100_000 })).StatusCode);
        var createAlert = await client.PostAsJsonAsync("/api/v1/price-alerts", new { productId = 1, targetPrice = 100_000 });
        Assert.Equal(HttpStatusCode.Created, createAlert.StatusCode);
        var alert = await createAlert.Content.ReadFromJsonAsync<JsonElement>();
        var alertId = alert.GetProperty("id").GetInt32();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(subject: "user-b"));
        var otherAlerts = await client.GetFromJsonAsync<JsonElement>("/api/v1/price-alerts");
        Assert.Empty(otherAlerts.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/price-alerts/{alertId}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.Token(subject: "user-a"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/price-alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/price-alerts/{alertId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/price-alerts/{alertId}")).StatusCode);
    }

    private static CompareShopDbContext CreateDatabase() => new(new DbContextOptionsBuilder<CompareShopDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static UserFeatureService CreateService(CompareShopDbContext db) =>
        new(new EfUserFeatureRepository(db, new FixedTimeProvider(new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero))));

    private static async Task<Product> AddProduct(CompareShopDbContext db, string slug, bool active = true)
    {
        var category = new Category { Slug = $"category-{slug}", Name = "Category" };
        var product = new Product { Slug = slug, Name = slug, Category = category, IsActive = active };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }
}

internal sealed class ConcurrentUserInsertInterceptor(string databaseName, string subject) : SaveChangesInterceptor
{
    public bool RaceSimulated { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!RaceSimulated && eventData.Context!.ChangeTracker.Entries<User>().Any(x => x.State == EntityState.Added))
        {
            RaceSimulated = true;
            var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(databaseName).Options;
            await using var concurrentRequest = new CompareShopDbContext(options);
            concurrentRequest.Users.Add(new User(subject, DateTime.UtcNow));
            await concurrentRequest.SaveChangesAsync(cancellationToken);
            throw new DbUpdateException("Simulated another request inserting the same unique subject first.");
        }
        return result;
    }
}
