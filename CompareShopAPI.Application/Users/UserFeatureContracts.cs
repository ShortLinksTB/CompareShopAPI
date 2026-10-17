using CompareShopAPI.Domain.Users;

namespace CompareShopAPI.Application.Users;

public sealed record WishlistEntry(int ProductId, string ProductSlug, string ProductName, DateTime AddedAtUtc);
public sealed record PriceAlertEntry(int Id, int ProductId, string ProductSlug, string ProductName, int TargetPrice, DateTime CreatedAtUtc);
public sealed record UserFeatureResult<T>(T? Value, UserFeatureError? Error)
{
    public bool Succeeded => Error is null;
    public static UserFeatureResult<T> Success(T value) => new(value, null);
    public static UserFeatureResult<T> Failure(UserFeatureError error) => new(default, error);
}
public enum UserFeatureError { InvalidInput, ProductNotFound, NotFound }
public enum WishlistOperation { Add, Remove }

public interface IUserFeatureRepository
{
    Task<int> GetOrCreateUserId(string subject, CancellationToken ct);
    Task<bool> IsActiveProduct(int productId, CancellationToken ct);
    Task<IReadOnlyList<WishlistEntry>> GetWishlist(int userId, CancellationToken ct);
    Task SetWishlistItem(int userId, int productId, bool shouldExist, CancellationToken ct);
    Task<IReadOnlyList<PriceAlertEntry>> GetPriceAlerts(int userId, CancellationToken ct);
    Task<PriceAlertEntry?> CreatePriceAlert(int userId, int productId, int targetPrice, CancellationToken ct);
    Task<bool> DeactivatePriceAlert(int userId, int alertId, CancellationToken ct);
}

public sealed class UserFeatureService(IUserFeatureRepository repository)
{
    public Task<int> EnsureUser(string subject, CancellationToken ct) => repository.GetOrCreateUserId(subject, ct);

    public async Task<IReadOnlyList<WishlistEntry>> Wishlist(string subject, CancellationToken ct)
    {
        var userId = await EnsureUser(subject, ct);
        return await repository.GetWishlist(userId, ct);
    }

    public async Task<UserFeatureResult<bool>> ChangeWishlist(string subject, int productId, string? operation, CancellationToken ct)
    {
        var parsed = operation?.ToLowerInvariant() switch
        {
            "add" => WishlistOperation.Add,
            "remove" => WishlistOperation.Remove,
            _ => (WishlistOperation?)null
        };
        if (productId <= 0 || parsed is null)
            return UserFeatureResult<bool>.Failure(UserFeatureError.InvalidInput);
        var userId = await EnsureUser(subject, ct);
        if (!await repository.IsActiveProduct(productId, ct)) return UserFeatureResult<bool>.Failure(UserFeatureError.ProductNotFound);
        await repository.SetWishlistItem(userId, productId, parsed == WishlistOperation.Add, ct);
        return UserFeatureResult<bool>.Success(true);
    }

    public async Task<IReadOnlyList<PriceAlertEntry>> PriceAlerts(string subject, CancellationToken ct)
    {
        var userId = await EnsureUser(subject, ct);
        return await repository.GetPriceAlerts(userId, ct);
    }

    public async Task<UserFeatureResult<PriceAlertEntry>> CreatePriceAlert(string subject, int productId, int targetPrice, CancellationToken ct)
    {
        if (productId <= 0 || targetPrice <= 0) return UserFeatureResult<PriceAlertEntry>.Failure(UserFeatureError.InvalidInput);
        var userId = await EnsureUser(subject, ct);
        if (!await repository.IsActiveProduct(productId, ct)) return UserFeatureResult<PriceAlertEntry>.Failure(UserFeatureError.ProductNotFound);
        var alert = await repository.CreatePriceAlert(userId, productId, targetPrice, ct);
        return alert is null
            ? UserFeatureResult<PriceAlertEntry>.Failure(UserFeatureError.ProductNotFound)
            : UserFeatureResult<PriceAlertEntry>.Success(alert);
    }

    public async Task<UserFeatureResult<bool>> DeletePriceAlert(string subject, int alertId, CancellationToken ct)
    {
        if (alertId <= 0) return UserFeatureResult<bool>.Failure(UserFeatureError.NotFound);
        var userId = await EnsureUser(subject, ct);
        return await repository.DeactivatePriceAlert(userId, alertId, ct)
            ? UserFeatureResult<bool>.Success(true)
            : UserFeatureResult<bool>.Failure(UserFeatureError.NotFound);
    }
}
