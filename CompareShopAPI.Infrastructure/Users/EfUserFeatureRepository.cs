using CompareShopAPI.Application.Users;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Users;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Users;

public sealed class EfUserFeatureRepository(CompareShopDbContext db, TimeProvider clock) : IUserFeatureRepository
{
    public async Task<int> GetOrCreateUserId(string subject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("A validated subject is required.", nameof(subject));
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.ExternalSubject == subject, ct);
        if (user is not null) return user.Id;

        user = new User(subject, clock.GetUtcNow().UtcDateTime);
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
            return user.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(user).State = EntityState.Detached;
            var concurrentUser = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.ExternalSubject == subject, ct);
            if (concurrentUser is not null) return concurrentUser.Id;
            throw;
        }
    }

    public Task<bool> IsActiveProduct(int productId, CancellationToken ct) =>
        db.Products.AnyAsync(x => x.Id == productId && x.IsActive, ct);

    public async Task<IReadOnlyList<WishlistEntry>> GetWishlist(int userId, CancellationToken ct) =>
        await (from item in db.WishlistItems.AsNoTracking()
            join product in db.Products.AsNoTracking() on item.ProductId equals product.Id
            where item.UserId == userId && product.IsActive
            orderby item.CreatedAtUtc descending
            select new WishlistEntry(item.ProductId, product.Slug, product.Name, item.CreatedAtUtc))
            .ToListAsync(ct);

    public async Task SetWishlistItem(int userId, int productId, bool shouldExist, CancellationToken ct)
    {
        var item = await db.WishlistItems.SingleOrDefaultAsync(x => x.UserId == userId && x.ProductId == productId, ct);
        if (!shouldExist)
        {
            if (item is null) return;
            db.WishlistItems.Remove(item);
            await db.SaveChangesAsync(ct);
            return;
        }
        if (item is not null) return;

        db.WishlistItems.Add(new WishlistItem(userId, productId, clock.GetUtcNow().UtcDateTime));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.WishlistItems.AnyAsync(x => x.UserId == userId && x.ProductId == productId, ct)) throw;
        }
    }

    public async Task<IReadOnlyList<PriceAlertEntry>> GetPriceAlerts(int userId, CancellationToken ct) =>
        await (from alert in db.PriceAlerts.AsNoTracking()
            join product in db.Products.AsNoTracking() on alert.ProductId equals product.Id
            where alert.UserId == userId && alert.IsActive && product.IsActive
            orderby alert.CreatedAtUtc descending
            select new PriceAlertEntry(alert.Id, alert.ProductId, product.Slug, product.Name, alert.TargetPrice, alert.CreatedAtUtc))
            .ToListAsync(ct);

    public async Task<PriceAlertEntry?> CreatePriceAlert(int userId, int productId, int targetPrice, CancellationToken ct)
    {
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == productId && x.IsActive, ct);
        if (product is null) return null;
        var alert = new PriceAlert(userId, productId, targetPrice, clock.GetUtcNow().UtcDateTime);
        db.PriceAlerts.Add(alert);
        await db.SaveChangesAsync(ct);
        return new PriceAlertEntry(alert.Id, product.Id, product.Slug, product.Name, alert.TargetPrice, alert.CreatedAtUtc);
    }

    public async Task<bool> DeactivatePriceAlert(int userId, int alertId, CancellationToken ct)
    {
        var alert = await db.PriceAlerts.SingleOrDefaultAsync(x => x.Id == alertId && x.UserId == userId && x.IsActive, ct);
        if (alert is null) return false;
        alert.Deactivate();
        await db.SaveChangesAsync(ct);
        return true;
    }
}
