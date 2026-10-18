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
public sealed class AdminFeatureFlag { public int Id { get; set; } public required string Key { get; set; } public bool Enabled { get; set; } public string? Description { get; set; } }
public sealed class AdminSetting { public int Id { get; set; } public required string Key { get; set; } public required string Value { get; set; } }
public sealed class ProductMatching { public int Id { get; set; } public required string Name { get; set; } public string? Brand { get; set; } public int? ProductId { get; set; } public DateTime CreatedAtUtc { get; set; } public bool IsApproved { get; set; } }
