using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;

namespace CompareShopAPI.Infrastructure.Catalog;

internal static class BannedItemVisibility
{
    public static IQueryable<Product> ExcludeBannedProducts(CompareShopDbContext db, IQueryable<Product> products) =>
        products.Where(product => !db.BannedItems.Any(ban => ban.IsActive &&
            (ban.TargetType == BannedTargetType.Product && ban.ProductId == product.Id
             || ban.TargetType == BannedTargetType.Brand && product.Brand != null
                && ban.NormalizedBrandName == product.Brand.Trim().ToUpper())));

    public static IQueryable<ProductOffer> ExcludeBannedProducts(CompareShopDbContext db, IQueryable<ProductOffer> offers) =>
        offers.Where(offer => !db.BannedItems.Any(ban => ban.IsActive &&
            (ban.TargetType == BannedTargetType.Product && ban.ProductId == offer.ProductId
             || ban.TargetType == BannedTargetType.Brand && offer.Product.Brand != null
                && ban.NormalizedBrandName == offer.Product.Brand.Trim().ToUpper())));
}
