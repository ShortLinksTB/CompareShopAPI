using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CompareShopAPI.Infrastructure.Catalog;

public sealed class EfAffiliateRedirectRepository(CompareShopDbContext db, ILogger<EfAffiliateRedirectRepository> logger) : IAffiliateRedirectRepository
{
    public Task<AffiliateLink?> FindRedirectableLink(int offerId, CancellationToken ct) => db.AffiliateLinks.AsNoTracking()
        .Where(link => link.ProductOfferId == offerId && link.ProductOffer.IsActive && !link.ProductOffer.IsOutlier &&
            link.ProductOffer.Product.IsActive && link.ProductOffer.Product.Category.IsActive && link.ProductOffer.PartnerShop.IsActive)
        .FirstOrDefaultAsync(ct);

    public async Task<bool> RecordClick(int affiliateLinkId, DateTime clickedAtUtc, string? clientIpDigest, CancellationToken ct)
    {
        try
        {
            if (!db.Database.IsRelational())
            {
                var link = await db.AffiliateLinks.SingleOrDefaultAsync(x => x.Id == affiliateLinkId, ct);
                if (link is null) return false;
                link.ClickCount++;
                db.AffiliateClicks.Add(new AffiliateClick { AffiliateLinkId = affiliateLinkId, ClickedAtUtc = clickedAtUtc, ClientIpDigest = clientIpDigest });
                await db.SaveChangesAsync(ct);
                return true;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.AffiliateLinks.Where(x => x.Id == affiliateLinkId && x.IsEnabled && !x.IsStale)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), ct);
            if (changed != 1)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }
            db.AffiliateClicks.Add(new AffiliateClick { AffiliateLinkId = affiliateLinkId, ClickedAtUtc = clickedAtUtc, ClientIpDigest = clientIpDigest });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Affiliate click could not be persisted for link {AffiliateLinkId}.", affiliateLinkId);
            return false;
        }
    }
}
