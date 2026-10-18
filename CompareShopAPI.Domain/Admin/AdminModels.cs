namespace CompareShopAPI.Domain.Admin;

public sealed class AdminRole { public int Id { get; set; } public required string Name { get; set; } }
public sealed class AdminPermission { public int Id { get; set; } public required string Key { get; set; } }
public sealed class AdminRolePermission { public int RoleId { get; set; } public int PermissionId { get; set; } }
public sealed class AdminUserRole { public int UserId { get; set; } public int RoleId { get; set; } }
public sealed class AdminAuditLog
{
    public long Id { get; set; }
    public required string ActorSubject { get; set; }
    public required string Action { get; set; }
    public required string ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}
public sealed class AdminFeatureFlag
{
    private AdminFeatureFlag() { }

    public AdminFeatureFlag(string key, bool enabled, string? description)
    {
        Key = NormalizeKey(key);
        Enabled = enabled;
        Description = NormalizeDescription(description);
    }

    public int Id { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    public string? Description { get; private set; }

    public void Update(bool enabled, string? description)
    {
        Enabled = enabled;
        Description = NormalizeDescription(description);
    }

    public static string NormalizeKey(string? key)
    {
        key = key?.Trim() ?? string.Empty;
        if (key.Length is < 1 or > 120 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new ArgumentException("Invalid feature flag key.", nameof(key));
        return key;
    }

    public static string? NormalizeDescription(string? description)
    {
        description = description?.Trim();
        if (description?.Length > 500) throw new ArgumentException("Feature flag description exceeds 500 characters.", nameof(description));
        return description;
    }
}

public sealed class AdminSetting
{
    private static readonly HashSet<string> SupportedKeys = ["price_alert_default_threshold", "offers_display_count"];

    private AdminSetting() { }

    public AdminSetting(string key, string value)
    {
        Key = key;
        Value = ValidateValue(key, value);
    }

    public int Id { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;

    public void UpdateValue(string value) => Value = ValidateValue(Key, value);

    public static IReadOnlyDictionary<string, string> ValidateUpdates(IReadOnlyDictionary<string, string>? updates)
    {
        if (updates is null || updates.Count == 0 || updates.Keys.Any(key => !SupportedKeys.Contains(key)))
            throw new ArgumentException("Only supported settings may be changed.", nameof(updates));
        return updates.ToDictionary(pair => pair.Key, pair => ValidateValue(pair.Key, pair.Value), StringComparer.Ordinal);
    }

    private static string ValidateValue(string key, string? value)
    {
        if (!SupportedKeys.Contains(key) || !int.TryParse(value, out var number)
            || (key == "price_alert_default_threshold" ? number <= 0 : number is < 1 or > 50))
            throw new ArgumentException($"Invalid value for {key}.", nameof(value));
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class ProductMatching
{
    private ProductMatching() { }

    public ProductMatching(string name, string? brand, DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 300) throw new ArgumentException("A matching name up to 300 characters is required.", nameof(name));
        Name = name;
        Brand = brand;
        CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
    }

    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Brand { get; private set; }
    public int? ProductId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool IsApproved { get; private set; }

    public bool TryApprove(int productId)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId), "Product ID must be positive.");
        if (ProductId is not null || IsApproved) return false;
        ProductId = productId;
        IsApproved = true;
        return true;
    }
}
