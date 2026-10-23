using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Data;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainFeatureFlag = CompareShopAPI.Domain.Admin.AdminFeatureFlag;
using DomainSetting = CompareShopAPI.Domain.Admin.AdminSetting;
using DomainUserRole = CompareShopAPI.Domain.Admin.AdminUserRole;
using DomainAuditLog = CompareShopAPI.Domain.Admin.AdminAuditLog;
using DomainUser = CompareShopAPI.Domain.UserCommerce.User;

namespace CompareShopAPI.Infrastructure.Admin;

public sealed class EfAdminData(CompareShopDbContext db, TimeProvider clock) : IAdminData, IAdminPermissionReader
{
    public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => db.AdminUserRoles.AsNoTracking()
        .Join(db.Users.AsNoTracking(), ur => ur.UserId, user => user.Id, (ur, user) => new { ur.RoleId, user.KeycloakSubject })
        .Where(ur => ur.KeycloakSubject == subject)
        .Join(db.AdminRolePermissions, ur => ur.RoleId, rp => rp.RoleId, (_, rp) => rp.PermissionId)
        .Join(db.AdminPermissions, id => id, p => p.Id, (_, p) => p.Key).AnyAsync(key => key == permission, ct);

    public async Task<IReadOnlyList<string>> PermissionsForSubject(string subject, CancellationToken ct) => await db.AdminUserRoles.AsNoTracking()
        .Join(db.Users.AsNoTracking(), ur => ur.UserId, user => user.Id, (ur, user) => new { ur.RoleId, user.KeycloakSubject })
        .Where(ur => ur.KeycloakSubject == subject)
        .Join(db.AdminRolePermissions.AsNoTracking(), ur => ur.RoleId, rp => rp.RoleId, (_, rp) => rp.PermissionId)
        .Join(db.AdminPermissions.AsNoTracking(), id => id, permission => permission.Id, (_, permission) => permission.Key)
        .Distinct()
        .OrderBy(key => key)
        .ToListAsync(ct);

    public async Task<bool> BootstrapFirstAdmin(string subject, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var role = await db.AdminRoles.SingleOrDefaultAsync(x => x.Name == "Super Admin", ct)
            ?? throw new InvalidOperationException("The Super Admin role is not seeded.");

        var user = await db.Users.SingleOrDefaultAsync(x => x.KeycloakSubject == subject, ct);
        if (user is not null && await db.AdminUserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == role.Id, ct))
        {
            await tx.CommitAsync(ct);
            return true;
        }

        if (await db.AdminUserRoles.AnyAsync(x => x.RoleId == role.Id, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        user ??= new DomainUser(subject, clock.GetUtcNow().UtcDateTime);
        if (db.Entry(user).State == EntityState.Detached) db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        db.AdminUserRoles.Add(new DomainUserRole { UserId = user.Id, RoleId = role.Id });
        db.AdminAuditLogs.Add(new DomainAuditLog
        {
            ActorSubject = subject,
            Action = "bootstrap_first_admin",
            ResourceType = "user",
            ResourceId = user.Id.ToString(),
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            BeforeJson = "[]",
            AfterJson = "[\"Super Admin\"]"
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public Task<Page<AdminCategory>> Categories(int page, int size, CancellationToken ct) => PageOf(db.Categories.AsNoTracking().OrderBy(x => x.Id).Select(x => new AdminCategory(x.Id, x.Slug, x.Name, x.IsActive)), page, size, ct);
    public Task<AdminCategory?> Category(int id, CancellationToken ct) => db.Categories.AsNoTracking().Where(x => x.Id == id).Select(x => new AdminCategory(x.Id, x.Slug, x.Name, x.IsActive)).SingleOrDefaultAsync(ct);
    public async Task<AdminCategory> SaveCategory(int? id, CategoryInput input, CancellationToken ct)
    {
        var item = id.HasValue ? await db.Categories.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (id.HasValue && item is null) throw new KeyNotFoundException();
        item ??= new Category { Slug = input.Slug, Name = input.Name };
        item.Slug = input.Slug; item.Name = input.Name; item.IsActive = input.IsActive;
        if (!id.HasValue) db.Categories.Add(item);
        await db.SaveChangesAsync(ct); return new(item.Id, item.Slug, item.Name, item.IsActive);
    }
    public async Task<bool> DeleteCategory(int id, CancellationToken ct) { var rows = await db.Categories.Where(x => x.Id == id).ExecuteDeleteAsync(ct); return rows > 0; }

    public Task<Page<AdminShop>> Shops(int page, int size, CancellationToken ct) => PageOf(db.PartnerShops.AsNoTracking().OrderBy(x => x.Id).Select(x => new AdminShop(x.Id, x.Slug, x.Name, x.WebsiteUrl, x.IsActive)), page, size, ct);
    public Task<AdminShop?> Shop(int id, CancellationToken ct) => db.PartnerShops.AsNoTracking().Where(x => x.Id == id).Select(x => new AdminShop(x.Id, x.Slug, x.Name, x.WebsiteUrl, x.IsActive)).SingleOrDefaultAsync(ct);
    public async Task<AdminShop> SaveShop(int? id, ShopInput input, CancellationToken ct)
    {
        var item = id.HasValue ? await db.PartnerShops.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (id.HasValue && item is null) throw new KeyNotFoundException();
        item ??= new PartnerShop { Slug = input.Slug, Name = input.Name };
        item.Slug = input.Slug; item.Name = input.Name; item.WebsiteUrl = input.WebsiteUrl; item.IsActive = input.IsActive;
        if (!id.HasValue) db.PartnerShops.Add(item);
        await db.SaveChangesAsync(ct); return new(item.Id, item.Slug, item.Name, item.WebsiteUrl, item.IsActive);
    }
    public async Task<bool> DeleteShop(int id, CancellationToken ct) { var rows = await db.PartnerShops.Where(x => x.Id == id).ExecuteDeleteAsync(ct); return rows > 0; }

    public Task<Page<AdminProduct>> Products(int page, int size, CancellationToken ct) => PageOf(db.Products.AsNoTracking().OrderBy(x => x.Id).Select(ProductDto), page, size, ct);
    public Task<AdminProduct?> Product(int id, CancellationToken ct) => db.Products.AsNoTracking().Where(x => x.Id == id).Select(ProductDto).SingleOrDefaultAsync(ct);
    public async Task<AdminProduct> SaveProduct(int? id, ProductInput input, CancellationToken ct)
    {
        if (!await db.Categories.AnyAsync(x => x.Id == input.CategoryId, ct)) throw new ArgumentException("Category does not exist.");
        var item = id.HasValue ? await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct) : null;
        if (id.HasValue && item is null) throw new KeyNotFoundException();
        item ??= new Product { Slug = input.Slug, Name = input.Name };
        item.Slug = input.Slug; item.Name = input.Name; item.Brand = input.Brand; item.Description = input.Description; item.CategoryId = input.CategoryId; item.IsActive = input.IsActive;
        if (!id.HasValue) db.Products.Add(item);
        await db.SaveChangesAsync(ct); return ToDto(item);
    }
    public async Task<bool> DeleteProduct(int id, CancellationToken ct) { var rows = await db.Products.Where(x => x.Id == id).ExecuteDeleteAsync(ct); return rows > 0; }

    public async Task<IReadOnlyList<AdminOffer>> Offers(CancellationToken ct) => await db.ProductOffers.AsNoTracking().OrderBy(x => x.Id)
        .Select(x => new AdminOffer(x.Id, x.ProductId, x.PartnerShopId, x.PriceVnd, x.Url, x.IsActive, x.IsOutlier, x.UpdatedAtUtc)).ToListAsync(ct);
    public async Task<AdminOffer?> SaveOffer(int id, OfferInput input, CancellationToken ct)
    {
        var item = await db.ProductOffers.SingleOrDefaultAsync(x => x.Id == id, ct); if (item is null) return null;
        item.PriceVnd = input.PriceVnd; item.Url = input.Url; item.IsActive = input.IsActive; item.IsOutlier = input.IsOutlier; item.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct); return new(item.Id, item.ProductId, item.PartnerShopId, item.PriceVnd, item.Url, item.IsActive, item.IsOutlier, item.UpdatedAtUtc);
    }
    public async Task<AdminOffer?> ApproveOffer(int id, CancellationToken ct)
    {
        var item = await db.ProductOffers.SingleOrDefaultAsync(x => x.Id == id, ct); if (item is null) return null;
        item.IsActive = true; item.IsOutlier = false; item.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct); return new(item.Id, item.ProductId, item.PartnerShopId, item.PriceVnd, item.Url, item.IsActive, item.IsOutlier, item.UpdatedAtUtc);
    }

    public async Task<IReadOnlyList<AdminMatching>> Matching(CancellationToken ct) => await db.ProductMatchings.AsNoTracking().Where(x => x.ProductId == null && !x.IsApproved).OrderBy(x => x.CreatedAtUtc).Select(MatchingDto).ToListAsync(ct);
    public async Task<CompareShopAPI.Domain.Admin.ProductMatching?> MatchingForApproval(int id, int productId, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(x => x.Id == productId && x.IsActive, ct)) throw new ArgumentException("Active product does not exist.");
        return await db.ProductMatchings.SingleOrDefaultAsync(x => x.Id == id, ct);
    }
    public async Task SaveMatching(CompareShopAPI.Domain.Admin.ProductMatching matching, CancellationToken ct)
    {
        if (matching.DataSourceId is int sourceId && matching.ExternalSku is string sku && matching.ProductId is int productId)
        {
            var mapping = await db.SourceItemMappings.SingleAsync(x => x.DataSourceId == sourceId && x.ExternalSku == sku, ct);
            mapping.Approve(productId);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Page<AdminAuditEntry>> AuditLogs(int page, int size, CancellationToken ct)
    {
        var q = db.AdminAuditLogs.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id);
        var total = await q.CountAsync(ct); var rows = await q.Skip(Offset(page, size)).Take(BoundSize(size)).ToListAsync(ct);
        return new(rows.Select(x => new AdminAuditEntry(x.Id, x.ActorSubject, x.Action, x.ResourceType, x.ResourceId, x.CreatedAtUtc, Parse(x.BeforeJson), Parse(x.AfterJson))).ToArray(), page, BoundSize(size), total);
    }
    public async Task<AdminDashboard> Dashboard(CancellationToken ct) => new(await db.Products.CountAsync(ct), await db.ProductOffers.CountAsync(ct), 0, false);
    public async Task<Page<AdminPriceAlert>> PriceAlerts(int page, int size, int? productId, int? userId, CancellationToken ct)
    {
        var q = db.PriceAlerts.AsNoTracking().Where(x => !productId.HasValue || x.ProductId == productId).Where(x => !userId.HasValue || x.UserId == userId).OrderByDescending(x => x.CreatedAtUtc);
        var total = await q.CountAsync(ct); var items = await q.Skip(Offset(page, size)).Take(BoundSize(size)).Select(x => new AdminPriceAlert(x.Id, x.UserId, x.ProductId, x.Product.Slug, x.Product.Name, x.TargetPrice, x.IsActive, x.CreatedAtUtc)).ToListAsync(ct);
        return new(items, page, BoundSize(size), total);
    }
    public async Task<AdminAnalytics> Analytics(CancellationToken ct) => new(
        await db.AffiliateLinks.SumAsync(x => (long?)x.ClickCount, ct) ?? 0,
        await db.AffiliateLinks.SumAsync(x => (long?)x.ConversionCount, ct) ?? 0,
        await db.AffiliateLinks.SumAsync(x => (decimal?)x.Revenue, ct) ?? 0m);

    public Task<AdminProduct?> SeoProduct(int id, CancellationToken ct) => db.Products.AsNoTracking().Where(x => x.Id == id).Select(ProductDto).SingleOrDefaultAsync(ct);
    public async Task<AdminProduct?> SaveSeoProduct(int id, SeoInput input, CancellationToken ct)
    {
        var item = await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct); if (item is null) return null;
        item.SeoTitle = input.Title?.Trim(); item.SeoDescription = input.Description?.Trim(); await db.SaveChangesAsync(ct); return ToDto(item);
    }

    public async Task<Page<AdminUser>> Users(int page, int size, CancellationToken ct)
    {
        var q = db.Users.AsNoTracking().OrderBy(x => x.Id); var total = await q.CountAsync(ct);
        var users = await q.Skip(Offset(page, size)).Take(BoundSize(size)).Select(x => new { x.Id, x.CreatedAtUtc, x.KeycloakSubject }).ToListAsync(ct);
        var ids = users.Select(x => x.Id).ToArray();
        var roles = await db.AdminUserRoles.AsNoTracking().Where(x => ids.Contains(x.UserId)).Join(db.AdminRoles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name }).ToListAsync(ct);
        return new(users.Select(x => new AdminUser(x.Id, x.CreatedAtUtc, roles.Where(r => r.UserId == x.Id).Select(r => r.Name).Order().ToArray()) { KeycloakSubject = x.KeycloakSubject }).ToArray(), page, BoundSize(size), total);
    }
    public async Task<AdminUser?> User(int id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.CreatedAtUtc }).SingleOrDefaultAsync(ct);
        if (user is null) return null;
        var roles = await db.AdminUserRoles.AsNoTracking().Where(x => x.UserId == id).Join(db.AdminRoles, ur => ur.RoleId, role => role.Id, (_, role) => role.Name).Order().ToArrayAsync(ct);
        return new AdminUser(user.Id, user.CreatedAtUtc, roles);
    }
    public async Task<AdminRoleAssignmentSnapshot?> RoleAssignmentSnapshot(int userId, IReadOnlyList<int> roleIds, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return null;
        var currentRoleIds = await db.AdminUserRoles.AsNoTracking().Where(x => x.UserId == userId).OrderBy(x => x.RoleId).Select(x => x.RoleId).ToListAsync(ct);
        if (roleIds.Count > 0 && await db.AdminRoles.CountAsync(x => roleIds.Contains(x.Id), ct) != roleIds.Distinct().Count()) throw new ArgumentException("A role does not exist.");
        var currentPermissions = await PermissionKeys(currentRoleIds, ct);
        var requestedPermissions = await PermissionKeys(roleIds, ct);
        return new(user.KeycloakSubject, currentRoleIds, currentPermissions, requestedPermissions);
    }

    public async Task<IReadOnlyList<string>?> ReplaceUserRoles(string actor, int userId, IReadOnlyList<int> roleIds, IReadOnlyList<int> expectedCurrentRoleIds, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct); if (user is null) return null;
        var currentRoles = await db.AdminUserRoles.Where(x => x.UserId == userId).OrderBy(x => x.RoleId).Select(x => x.RoleId).ToListAsync(ct);
        if (!currentRoles.SequenceEqual(expectedCurrentRoleIds)) return null;
        var beforeNames = await db.AdminRoles.AsNoTracking().Where(x => currentRoles.Contains(x.Id)).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(ct);
        if (roleIds.Count > 0 && await db.AdminRoles.CountAsync(x => roleIds.Contains(x.Id), ct) != roleIds.Distinct().Count()) throw new ArgumentException("A role does not exist.");
        await db.AdminUserRoles.Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        db.AdminUserRoles.AddRange(roleIds.Select(roleId => new DomainUserRole { UserId = userId, RoleId = roleId }));
        await db.SaveChangesAsync(ct);
        var afterNames = await db.AdminUserRoles.AsNoTracking().Where(x => x.UserId == userId).Join(db.AdminRoles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name).Order().ToListAsync(ct);
        db.AdminAuditLogs.Add(new DomainAuditLog { ActorSubject = actor, Action = "replace_role_assignments", ResourceType = "user", ResourceId = userId.ToString(), CreatedAtUtc = clock.GetUtcNow().UtcDateTime, BeforeJson = System.Text.Json.JsonSerializer.Serialize(beforeNames), AfterJson = System.Text.Json.JsonSerializer.Serialize(afterNames) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return afterNames;
    }
    private async Task<HashSet<string>> PermissionKeys(IReadOnlyCollection<int> roles, CancellationToken ct) => (await db.AdminRolePermissions.AsNoTracking().Where(x => roles.Contains(x.RoleId)).Join(db.AdminPermissions, rp => rp.PermissionId, p => p.Id, (_, p) => p.Key).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    public async Task<IReadOnlyList<AdminFeatureFlag>> FeatureFlags(CancellationToken ct) => await db.AdminFeatureFlags.AsNoTracking().OrderBy(x => x.Key).Select(x => new AdminFeatureFlag(x.Id, x.Key, x.Enabled, x.Description)).ToListAsync(ct);
    public async Task<AdminFeatureFlag> SaveFeatureFlag(string key, FeatureFlagInput input, CancellationToken ct)
    {
        var item = await db.AdminFeatureFlags.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (item is null) { item = new DomainFeatureFlag(key, input.Enabled, input.Description); db.AdminFeatureFlags.Add(item); }
        else item.Update(input.Enabled, input.Description);
        await db.SaveChangesAsync(ct); return new(item.Id, item.Key, item.Enabled, item.Description);
    }
    public async Task<IReadOnlyList<AdminSetting>> Settings(CancellationToken ct) => await db.AdminSettings.AsNoTracking().OrderBy(x => x.Key).Select(x => new AdminSetting(x.Key, x.Value)).ToListAsync(ct);
    public async Task<IReadOnlyList<AdminSetting>?> SaveSettings(IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        foreach (var (key, value) in settings)
        {
            var row = await db.AdminSettings.SingleOrDefaultAsync(x => x.Key == key, ct);
            if (row is null) db.AdminSettings.Add(new DomainSetting(key, value)); else row.UpdateValue(value);
        }
        await db.SaveChangesAsync(ct); return await Settings(ct);
    }
    public async Task<T> ExecuteAudited<T>(string actor, string action, string resourceType, string? resourceId, Func<CancellationToken, Task<(T? Result, object? Before, object? After)>> operation, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (result, before, after) = await operation(ct);
        if (result is null) throw new KeyNotFoundException("Resource not found.");
        var resolvedId = resourceId ?? result?.GetType().GetProperty("Id")?.GetValue(result)?.ToString();
        db.AdminAuditLogs.Add(new DomainAuditLog { ActorSubject = actor, Action = action, ResourceType = resourceType, ResourceId = resolvedId, CreatedAtUtc = clock.GetUtcNow().UtcDateTime, BeforeJson = Redact(before), AfterJson = Redact(after) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }

    private static string? Redact(object? value)
    {
        if (value is null) return null;
        var node = JsonSerializer.SerializeToNode(value); RedactNode(node);
        return node?.ToJsonString();
    }
    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (Regex.IsMatch(key, "token|secret|password|credential|authorization|api.?key", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) obj[key] = "[REDACTED]";
                else RedactNode(value);
            }
        }
        else if (node is JsonArray array) foreach (var value in array) RedactNode(value);
        else if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text is not null)
        {
            var safe = Regex.Replace(text, "(?i)([?&](?:access_token|refresh_token|token|secret|api[_-]?key|password|signature)=)[^&#]*", "$1[REDACTED]");
            safe = Regex.Replace(safe, "(?i)Bearer\\s+[A-Za-z0-9._~-]+", "Bearer [REDACTED]");
            if (safe != text) scalar.ReplaceWith(JsonValue.Create(safe));
        }
    }
    private static System.Text.Json.JsonElement? Parse(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; using var doc = JsonDocument.Parse(value); return doc.RootElement.Clone(); }
    private static AdminProduct ToDto(Product x) => new(x.Id, x.Slug, x.Name, x.Brand, x.Description, x.SeoTitle, x.SeoDescription, x.CategoryId, x.IsActive);
    private static readonly System.Linq.Expressions.Expression<Func<Product, AdminProduct>> ProductDto = x => new AdminProduct(x.Id, x.Slug, x.Name, x.Brand, x.Description, x.SeoTitle, x.SeoDescription, x.CategoryId, x.IsActive);
    private static readonly System.Linq.Expressions.Expression<Func<CompareShopAPI.Domain.Admin.ProductMatching, AdminMatching>> MatchingDto = x => new AdminMatching(x.Id, x.Name, x.Brand, x.ProductId, x.CreatedAtUtc, x.IsApproved, x.ExternalSku, x.DataSourceId);
    private static int BoundSize(int size) => Math.Clamp(size, 1, 100);
    private static int Offset(int page, int size) => (int)Math.Min(int.MaxValue, ((long)Math.Max(1, page) - 1) * BoundSize(size));
    private static async Task<Page<T>> PageOf<T>(IQueryable<T> query, int page, int size, CancellationToken ct)
    { var actualPage = Math.Max(1, page); var actualSize = BoundSize(size); var total = await query.CountAsync(ct); var items = await query.Skip(Offset(actualPage, actualSize)).Take(actualSize).ToListAsync(ct); return new(items, actualPage, actualSize, total); }
}
