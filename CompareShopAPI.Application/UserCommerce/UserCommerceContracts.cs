using CompareShopAPI.Domain.UserCommerce;

namespace CompareShopAPI.Application.UserCommerce;

public sealed record WishlistItemDto(int ProductId, string Slug, string Name, DateTime CreatedAtUtc);
public sealed record PriceAlertDto(int Id, int ProductId, string Slug, string Name, int TargetPrice, bool IsActive, DateTime CreatedAtUtc);

public interface IUserProvisioning
{
    Task<int> GetOrCreate(string keycloakSubject, CancellationToken ct);
    Task<UserProvisioningResult> GetOrCreateForAdmin(string keycloakSubject, CancellationToken ct);
}

public sealed record UserProvisioningResult(int UserId, bool Created);

public sealed class AdminIdentityProvisioningService(IUserProvisioning users)
{
    public Task<UserProvisioningResult> Provision(string? subject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("A Keycloak subject is required.", nameof(subject));
        return users.GetOrCreateForAdmin(subject, ct);
    }
}

public interface IUserCommerceRepository
{
    Task<bool> IsActiveProduct(int productId, CancellationToken ct);
    Task<IReadOnlyList<WishlistItemDto>> Wishlist(int userId, CancellationToken ct);
    Task AddWishlist(int userId, int productId, DateTime nowUtc, CancellationToken ct);
    Task RemoveWishlist(int userId, int productId, CancellationToken ct);
    Task<IReadOnlyList<PriceAlertDto>> PriceAlerts(int userId, CancellationToken ct);
    Task<PriceAlertDto> AddPriceAlert(int userId, int productId, int targetPrice, DateTime nowUtc, CancellationToken ct);
    Task<bool> DeletePriceAlert(int userId, int alertId, CancellationToken ct);
}

public sealed class WishlistService(IUserProvisioning users, IUserCommerceRepository commerce)
{
    public async Task<IReadOnlyList<WishlistItemDto>> List(string subject, CancellationToken ct)
    {
        var userId = await users.GetOrCreate(subject, ct);
        return await commerce.Wishlist(userId, ct);
    }

    public async Task<bool> Change(string subject, int productId, string operation, DateTime nowUtc, CancellationToken ct)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (operation is not ("add" or "remove")) throw new ArgumentException("Operation must be add or remove.", nameof(operation));
        var userId = await users.GetOrCreate(subject, ct);
        if (!await commerce.IsActiveProduct(productId, ct)) return false;
        if (operation == "add") await commerce.AddWishlist(userId, productId, nowUtc, ct);
        else await commerce.RemoveWishlist(userId, productId, ct);
        return true;
    }
}

public sealed class PriceAlertService(IUserProvisioning users, IUserCommerceRepository commerce)
{
    public async Task<IReadOnlyList<PriceAlertDto>> List(string subject, CancellationToken ct)
    {
        var userId = await users.GetOrCreate(subject, ct);
        return await commerce.PriceAlerts(userId, ct);
    }

    public async Task<PriceAlertDto?> Create(string subject, int productId, int targetPrice, DateTime nowUtc, CancellationToken ct)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (targetPrice <= 0) throw new ArgumentOutOfRangeException(nameof(targetPrice));
        var userId = await users.GetOrCreate(subject, ct);
        if (!await commerce.IsActiveProduct(productId, ct)) return null;
        return await commerce.AddPriceAlert(userId, productId, targetPrice, nowUtc, ct);
    }

    public async Task<bool> Delete(string subject, int alertId, CancellationToken ct)
    {
        if (alertId <= 0) return false;
        var userId = await users.GetOrCreate(subject, ct);
        return await commerce.DeletePriceAlert(userId, alertId, ct);
    }
}
