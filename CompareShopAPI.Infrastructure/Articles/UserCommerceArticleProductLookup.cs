using CompareShopAPI.Application.Articles;
using CompareShopAPI.Application.UserCommerce;

namespace CompareShopAPI.Infrastructure.Articles;

public sealed class UserCommerceArticleProductLookup(IUserCommerceRepository commerce) : IArticleProductLookup
{
    public Task<bool> IsActive(int productId, CancellationToken ct) => commerce.IsActiveProduct(productId, ct);
}
