using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.UserCommerce;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class UserCommerceTests
{
    [Fact]
    public async Task Provisioning_recovers_when_a_concurrent_request_wins_the_unique_subject_insert()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var competingOptions = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(databaseName).Options;
        var interceptor = new ConcurrentUserInsertInterceptor(competingOptions);
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(databaseName).AddInterceptors(interceptor).Options;
        await using var db = new CompareShopDbContext(options);

        var userId = await new EfUserProvisioning(db, TimeProvider.System).GetOrCreate("concurrent-subject", default);

        Assert.Equal(interceptor.WinnerId, userId);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Provisioning_rejects_a_blank_subject_before_persistence()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new CompareShopDbContext(options);

        await Assert.ThrowsAsync<ArgumentException>(() => new EfUserProvisioning(db, TimeProvider.System).GetOrCreate(" ", default));
    }

    [Fact]
    public async Task AuthAPI_provisioning_is_lazy_subject_only_and_keeps_Keycloak_identity_separate()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new CompareShopDbContext(options);
        var legacy = new User("legacy-keycloak-subject", DateTime.UtcNow);
        db.Users.Add(legacy);
        await db.SaveChangesAsync();
        var provisioning = new EfUserProvisioning(db, TimeProvider.System);

        var firstId = await provisioning.GetOrCreateForAuth("stable-auth-subject", default);
        var secondId = await provisioning.GetOrCreateForAuth("stable-auth-subject", default);

        Assert.Equal(firstId, secondId);
        Assert.NotEqual(legacy.Id, firstId);
        var authUser = await db.Users.SingleAsync(user => user.Id == firstId);
        Assert.Null(authUser.KeycloakSubject);
        Assert.Equal("stable-auth-subject", authUser.AuthSubject);
        Assert.Equal("legacy-keycloak-subject", (await db.Users.SingleAsync(user => user.Id == legacy.Id)).KeycloakSubject);
        Assert.Equal(2, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Wishlist_keeps_a_dangling_product_reference_with_null_catalog_fields()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new CompareShopDbContext(options);
        var user = new User("wishlist-dangling-subject", DateTime.UtcNow);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.WishlistItems.Add(new WishlistItem(user.Id, 987654, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var result = await new EfUserCommerceRepository(db).Wishlist(user.Id, default);

        var item = Assert.Single(result);
        Assert.Equal(987654, item.ProductId);
        Assert.Null(item.Slug);
        Assert.Null(item.Name);
        Assert.Null(item.ImageUrl);
    }

    [Fact]
    public void Commerce_entities_reject_invalid_identity_product_and_target_price()
    {
        Assert.Throws<ArgumentException>(() => new User("", DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WishlistItem(0, 1, DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WishlistItem(1, 0, DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceAlert(0, 1, 10, DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceAlert(1, 0, 10, DateTime.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceAlert(1, 1, 0, DateTime.UtcNow));
    }

    private sealed class ConcurrentUserInsertInterceptor(DbContextOptions<CompareShopDbContext> competingOptions) : SaveChangesInterceptor
    {
        public int WinnerId { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await using var competingDb = new CompareShopDbContext(competingOptions);
            var winner = new User("concurrent-subject", DateTime.UtcNow);
            competingDb.Users.Add(winner);
            await competingDb.SaveChangesAsync(cancellationToken);
            WinnerId = winner.Id;
            throw new DbUpdateException("Simulated unique-subject race.");
        }
    }
}
