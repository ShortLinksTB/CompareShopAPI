using CompareShopAPI.Domain.Catalog;

namespace CompareShopAPI.Domain.Users;

public sealed class User
{
    private User() { }

    public User(string externalSubject, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(externalSubject)) throw new ArgumentException("A validated external subject is required.", nameof(externalSubject));
        ExternalSubject = externalSubject;
        CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
    }

    public int Id { get; private set; }
    public string ExternalSubject { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
}

public sealed class WishlistItem
{
    private WishlistItem() { }

    public WishlistItem(int userId, int productId, DateTime createdAtUtc)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        UserId = userId;
        ProductId = productId;
        CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
    }

    public int Id { get; private set; }
    public int UserId { get; private set; }
    public int ProductId { get; private set; }
    public Product Product { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
}

public sealed class PriceAlert
{
    private PriceAlert() { }

    public PriceAlert(int userId, int productId, int targetPrice, DateTime createdAtUtc)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (targetPrice <= 0) throw new ArgumentOutOfRangeException(nameof(targetPrice));
        UserId = userId;
        ProductId = productId;
        TargetPrice = targetPrice;
        CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        IsActive = true;
    }

    public int Id { get; private set; }
    public int UserId { get; private set; }
    public int ProductId { get; private set; }
    public Product Product { get; private set; } = null!;
    public int TargetPrice { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public void Deactivate() => IsActive = false;
}
