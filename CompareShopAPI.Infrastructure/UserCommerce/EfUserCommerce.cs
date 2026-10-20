using CompareShopAPI.Application.UserCommerce;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using CompareShopAPI.Infrastructure.Catalog;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.UserCommerce;

public sealed class EfUserProvisioning(CompareShopDbContext db, TimeProvider clock) : IUserProvisioning
{
    public async Task<int> GetOrCreate(string keycloakSubject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keycloakSubject)) throw new ArgumentException("A Keycloak subject is required.", nameof(keycloakSubject));
        var existing = await db.Users.AsNoTracking().Where(x => x.KeycloakSubject == keycloakSubject).Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);
        if (existing.HasValue) return existing.Value;

        var user = new User(keycloakSubject, clock.GetUtcNow().UtcDateTime);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return user.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(user).State = EntityState.Detached;
            var winner = await db.Users.AsNoTracking().Where(x => x.KeycloakSubject == keycloakSubject).Select(x => (int?)x.Id).SingleOrDefaultAsync(ct);
            if (winner.HasValue) return winner.Value;
            throw;
        }
    }
}

public sealed class EfUserCommerceRepository(CompareShopDbContext db) : IUserCommerceRepository
{
    public Task<bool> IsActiveProduct(int productId, CancellationToken ct) => db.Products.AsNoTracking().AnyAsync(x => x.Id == productId && x.IsActive
        && !db.BannedItems.Any(ban => ban.IsActive &&
            (ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Product && ban.ProductId == x.Id
             || ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Brand && x.Brand != null
                && ban.NormalizedBrandName == x.Brand.Trim().ToUpper())), ct);

    public async Task<IReadOnlyList<WishlistItemDto>> Wishlist(int userId, CancellationToken ct) => await db.WishlistItems.AsNoTracking()
        .Where(x => x.UserId == userId && x.Product.IsActive && !db.BannedItems.Any(ban => ban.IsActive &&
            (ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Product && ban.ProductId == x.ProductId
             || ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Brand && x.Product.Brand != null
                && ban.NormalizedBrandName == x.Product.Brand.Trim().ToUpper()))).OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.ProductId)
        .Select(x => new WishlistItemDto(x.ProductId, x.Product.Slug, x.Product.Name, x.CreatedAtUtc)).ToListAsync(ct);

    public async Task AddWishlist(int userId, int productId, DateTime nowUtc, CancellationToken ct)
    {
        if (await db.WishlistItems.AnyAsync(x => x.UserId == userId && x.ProductId == productId, ct)) return;
        db.WishlistItems.Add(new WishlistItem(userId, productId, nowUtc));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.WishlistItems.AnyAsync(x => x.UserId == userId && x.ProductId == productId, ct)) throw;
        }
    }

    public async Task RemoveWishlist(int userId, int productId, CancellationToken ct)
    {
        await db.WishlistItems.Where(x => x.UserId == userId && x.ProductId == productId).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<PriceAlertDto>> PriceAlerts(int userId, CancellationToken ct) => await db.PriceAlerts.AsNoTracking()
        .Where(x => x.UserId == userId && !db.BannedItems.Any(ban => ban.IsActive &&
            (ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Product && ban.ProductId == x.ProductId
             || ban.TargetType == CompareShopAPI.Domain.Admin.BannedTargetType.Brand && x.Product.Brand != null
                && ban.NormalizedBrandName == x.Product.Brand.Trim().ToUpper()))).OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
        .Select(x => new PriceAlertDto(x.Id, x.ProductId, x.Product.Slug, x.Product.Name, x.TargetPrice, x.IsActive, x.CreatedAtUtc)).ToListAsync(ct);

    public async Task<PriceAlertDto> AddPriceAlert(int userId, int productId, int targetPrice, DateTime nowUtc, CancellationToken ct)
    {
        var alert = new PriceAlert(userId, productId, targetPrice, nowUtc);
        db.PriceAlerts.Add(alert);
        await db.SaveChangesAsync(ct);
        return await db.PriceAlerts.AsNoTracking().Where(x => x.Id == alert.Id && x.UserId == userId)
            .Select(x => new PriceAlertDto(x.Id, x.ProductId, x.Product.Slug, x.Product.Name, x.TargetPrice, x.IsActive, x.CreatedAtUtc)).SingleAsync(ct);
    }

    public async Task<bool> DeletePriceAlert(int userId, int alertId, CancellationToken ct) =>
        await db.PriceAlerts.Where(x => x.UserId == userId && x.Id == alertId).ExecuteDeleteAsync(ct) > 0;
}
