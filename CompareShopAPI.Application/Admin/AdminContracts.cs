using System.Text.Json;
using DomainAdminFeatureFlag = CompareShopAPI.Domain.Admin.AdminFeatureFlag;
using DomainAdminSetting = CompareShopAPI.Domain.Admin.AdminSetting;
using DomainProductMatching = CompareShopAPI.Domain.Admin.ProductMatching;
using DomainRoleAssignment = CompareShopAPI.Domain.Admin.AdminRoleAssignment;

namespace CompareShopAPI.Application.Admin;

public static class AdminPermissions
{
    public const string ProductsRead = "products.read", ProductsCreate = "products.create", ProductsUpdate = "products.update", ProductsDelete = "products.delete";
    public const string CategoriesRead = "categories.read", CategoriesCreate = "categories.create", CategoriesUpdate = "categories.update", CategoriesDelete = "categories.delete";
    public const string ShopsRead = "shops.read", ShopsCreate = "shops.create", ShopsUpdate = "shops.update", ShopsDelete = "shops.delete";
    public const string OffersRead = "offers.read", OffersUpdate = "offers.update", OffersApprove = "offers.approve";
    public const string MatchingRead = "matching.read", MatchingApprove = "matching.approve";
    public const string AuditRead = "audit_logs.read", DashboardRead = "dashboard.read", PriceAlertsReadAll = "price_alerts.read_all", AnalyticsRead = "analytics.read";
    public const string SeoRead = "seo.read", SeoUpdate = "seo.update", UsersManage = "users.manage", RolesManage = "roles.manage";
    public const string FeatureFlagsRead = "feature_flags.read", FeatureFlagsUpdate = "feature_flags.update", SettingsManage = "settings.manage";
}

public sealed record AdminCategory(int Id, string Slug, string Name, bool IsActive);
public sealed record AdminShop(int Id, string Slug, string Name, string? WebsiteUrl, bool IsActive);
public sealed record AdminProduct(int Id, string Slug, string Name, string? Brand, string? Description, string? SeoTitle, string? SeoDescription, int CategoryId, bool IsActive);
public sealed record AdminOffer(int Id, int ProductId, int PartnerShopId, int PriceVnd, string? Url, bool IsActive, bool IsOutlier, DateTime UpdatedAtUtc);
public sealed record AdminMatching(int Id, string Name, string? Brand, int? ProductId, DateTime CreatedAtUtc, bool IsApproved);
public sealed record AdminAuditEntry(long Id, string Actor, string Action, string ResourceType, string? ResourceId, DateTime CreatedAtUtc, JsonElement? Before, JsonElement? After);
public sealed record AdminDashboard(int Products, int Offers, int Articles, bool ArticlesAvailable);
public sealed record AdminPriceAlert(int Id, int UserId, int ProductId, string ProductSlug, string ProductName, int TargetPrice, bool IsActive, DateTime CreatedAtUtc);
public sealed record AdminAnalytics(long Clicks, long Conversions, decimal Revenue);
public sealed record AdminUser(int Id, DateTime CreatedAtUtc, IReadOnlyList<string> Roles);
public sealed record AdminFeatureFlag(int Id, string Key, bool Enabled, string? Description);
public sealed record AdminSetting(string Key, string Value);
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalItems);
public sealed record CategoryInput(string Slug, string Name, bool IsActive = true);
public sealed record ShopInput(string Slug, string Name, string? WebsiteUrl, bool IsActive = true);
public sealed record ProductInput(string Slug, string Name, string? Brand, string? Description, int CategoryId, bool IsActive = true);
public sealed record OfferInput(int PriceVnd, string? Url, bool IsActive, bool IsOutlier);
public sealed record SeoInput(string? Title, string? Description);
public sealed record FeatureFlagInput(bool Enabled, string? Description);
public sealed record AdminRoleAssignmentSnapshot(
    string Subject,
    IReadOnlyList<int> CurrentRoleIds,
    IReadOnlySet<string> CurrentPermissionKeys,
    IReadOnlySet<string> RequestedPermissionKeys);

public interface IAdminData
{
    Task<bool> HasPermission(string subject, string permission, CancellationToken ct);
    Task<bool> BootstrapFirstAdmin(string subject, CancellationToken ct);
    Task<Page<AdminCategory>> Categories(int page, int pageSize, CancellationToken ct);
    Task<AdminCategory?> Category(int id, CancellationToken ct);
    Task<AdminCategory> SaveCategory(int? id, CategoryInput input, CancellationToken ct);
    Task<bool> DeleteCategory(int id, CancellationToken ct);
    Task<Page<AdminShop>> Shops(int page, int pageSize, CancellationToken ct);
    Task<AdminShop?> Shop(int id, CancellationToken ct);
    Task<AdminShop> SaveShop(int? id, ShopInput input, CancellationToken ct);
    Task<bool> DeleteShop(int id, CancellationToken ct);
    Task<Page<AdminProduct>> Products(int page, int pageSize, CancellationToken ct);
    Task<AdminProduct?> Product(int id, CancellationToken ct);
    Task<AdminProduct> SaveProduct(int? id, ProductInput input, CancellationToken ct);
    Task<bool> DeleteProduct(int id, CancellationToken ct);
    Task<IReadOnlyList<AdminOffer>> Offers(CancellationToken ct);
    Task<AdminOffer?> SaveOffer(int id, OfferInput input, CancellationToken ct);
    Task<AdminOffer?> ApproveOffer(int id, CancellationToken ct);
    Task<IReadOnlyList<AdminMatching>> Matching(CancellationToken ct);
    Task<DomainProductMatching?> MatchingForApproval(int id, int productId, CancellationToken ct);
    Task SaveMatching(DomainProductMatching matching, CancellationToken ct);
    Task<Page<AdminAuditEntry>> AuditLogs(int page, int pageSize, CancellationToken ct);
    Task<AdminDashboard> Dashboard(CancellationToken ct);
    Task<Page<AdminPriceAlert>> PriceAlerts(int page, int pageSize, int? productId, int? userId, CancellationToken ct);
    Task<AdminAnalytics> Analytics(CancellationToken ct);
    Task<AdminProduct?> SeoProduct(int id, CancellationToken ct);
    Task<AdminProduct?> SaveSeoProduct(int id, SeoInput input, CancellationToken ct);
    Task<Page<AdminUser>> Users(int page, int pageSize, CancellationToken ct);
    Task<AdminUser?> User(int id, CancellationToken ct);
    Task<AdminRoleAssignmentSnapshot?> RoleAssignmentSnapshot(int userId, IReadOnlyList<int> roleIds, CancellationToken ct);
    Task<IReadOnlyList<string>?> ReplaceUserRoles(string actor, int userId, IReadOnlyList<int> roleIds, IReadOnlyList<int> expectedCurrentRoleIds, CancellationToken ct);
    Task<IReadOnlyList<AdminFeatureFlag>> FeatureFlags(CancellationToken ct);
    Task<AdminFeatureFlag> SaveFeatureFlag(string key, FeatureFlagInput input, CancellationToken ct);
    Task<IReadOnlyList<AdminSetting>> Settings(CancellationToken ct);
    Task<IReadOnlyList<AdminSetting>?> SaveSettings(IReadOnlyDictionary<string, string> settings, CancellationToken ct);
    Task<T> ExecuteAudited<T>(string actor, string action, string resourceType, string? resourceId, Func<CancellationToken, Task<(T? Result, object? Before, object? After)>> operation, CancellationToken ct);
}

public sealed class AdminForbiddenException() : Exception("The caller does not have permission for this operation.");
public sealed class AdminUnauthorizedException() : Exception("A validated subject is required.");
public sealed class AdminBootstrapCompletedException() : Exception("The first admin has already been provisioned.");

public sealed class AdminService(IAdminData data, IReadOnlySet<string>? bootstrapSubjects = null)
{
    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        if (!await data.HasPermission(subject, permission, ct)) throw new AdminForbiddenException();
        return subject;
    }

    public async Task BootstrapFirstAdmin(string? subject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        if (bootstrapSubjects?.Contains(subject) != true) throw new AdminForbiddenException();
        if (!await data.BootstrapFirstAdmin(subject, ct)) throw new AdminBootstrapCompletedException();
    }

    private async Task<T> Write<T>(string? subject, string permission, string action, string resource, string? id, Func<CancellationToken, Task<(T? Result, object? Before, object? After)>> work, CancellationToken ct)
    {
        var actor = await Authorize(subject, permission, ct);
        return await data.ExecuteAudited(actor, action, resource, id, work, ct);
    }

    public async Task<Page<AdminCategory>> Categories(string? subject, int page, int size, CancellationToken ct) { await Authorize(subject, AdminPermissions.CategoriesRead, ct); ValidatePage(page, size); return await data.Categories(page, size, ct); }
    public async Task<AdminCategory?> Category(string? subject, int id, CancellationToken ct) { await Authorize(subject, AdminPermissions.CategoriesRead, ct); return await data.Category(id, ct); }
    public Task<AdminCategory> CreateCategory(string? s, CategoryInput i, CancellationToken ct) => Write<AdminCategory>(s, AdminPermissions.CategoriesCreate, "create", "category", null, async t => (await data.SaveCategory(null, Validate(i), t), null, i), ct);
    public Task<AdminCategory> UpdateCategory(string? s, int id, CategoryInput i, CancellationToken ct) => Write(s, AdminPermissions.CategoriesUpdate, "update", "category", id.ToString(), async t => { var before = await data.Category(id, t); return before is null ? (null, null, null) : (await data.SaveCategory(id, Validate(i), t), before, i); }, ct);
    public async Task DeleteCategory(string? s, int id, CancellationToken ct) { var actor = await Authorize(s, AdminPermissions.CategoriesDelete, ct); await data.ExecuteAudited<bool>(actor, "delete", "category", id.ToString(), async t => { var old = await data.Category(id, t); if (old is null || !await data.DeleteCategory(id, t)) throw new KeyNotFoundException(); return (true, old, null); }, ct); }

    public async Task<Page<AdminShop>> Shops(string? subject, int page, int size, CancellationToken ct) { await Authorize(subject, AdminPermissions.ShopsRead, ct); ValidatePage(page, size); return await data.Shops(page, size, ct); }
    public async Task<AdminShop?> Shop(string? subject, int id, CancellationToken ct) { await Authorize(subject, AdminPermissions.ShopsRead, ct); return await data.Shop(id, ct); }
    public Task<AdminShop> CreateShop(string? s, ShopInput i, CancellationToken ct) => Write<AdminShop>(s, AdminPermissions.ShopsCreate, "create", "shop", null, async t => (await data.SaveShop(null, Validate(i), t), null, i), ct);
    public Task<AdminShop> UpdateShop(string? s, int id, ShopInput i, CancellationToken ct) => Write(s, AdminPermissions.ShopsUpdate, "update", "shop", id.ToString(), async t => { var before = await data.Shop(id, t); return before is null ? (null, null, null) : (await data.SaveShop(id, Validate(i), t), before, i); }, ct);
    public async Task DeleteShop(string? s, int id, CancellationToken ct) { var actor = await Authorize(s, AdminPermissions.ShopsDelete, ct); await data.ExecuteAudited<bool>(actor, "delete", "shop", id.ToString(), async t => { var old = await data.Shop(id, t); if (old is null || !await data.DeleteShop(id, t)) throw new KeyNotFoundException(); return (true, old, null); }, ct); }

    public async Task<Page<AdminProduct>> Products(string? subject, int page, int size, CancellationToken ct) { await Authorize(subject, AdminPermissions.ProductsRead, ct); ValidatePage(page, size); return await data.Products(page, size, ct); }
    public async Task<AdminProduct?> Product(string? subject, int id, CancellationToken ct) { await Authorize(subject, AdminPermissions.ProductsRead, ct); return await data.Product(id, ct); }
    public Task<AdminProduct> CreateProduct(string? s, ProductInput i, CancellationToken ct) => Write<AdminProduct>(s, AdminPermissions.ProductsCreate, "create", "product", null, async t => (await data.SaveProduct(null, Validate(i), t), null, i), ct);
    public Task<AdminProduct> UpdateProduct(string? s, int id, ProductInput i, CancellationToken ct) => Write(s, AdminPermissions.ProductsUpdate, "update", "product", id.ToString(), async t => { var before = await data.Product(id, t); return before is null ? (null, null, null) : (await data.SaveProduct(id, Validate(i), t), before, i); }, ct);
    public async Task DeleteProduct(string? s, int id, CancellationToken ct) { var actor = await Authorize(s, AdminPermissions.ProductsDelete, ct); await data.ExecuteAudited<bool>(actor, "delete", "product", id.ToString(), async t => { var old = await data.Product(id, t); if (old is null || !await data.DeleteProduct(id, t)) throw new KeyNotFoundException(); return (true, old, null); }, ct); }

    public async Task<IReadOnlyList<AdminOffer>> Offers(string? s, CancellationToken ct) { await Authorize(s, AdminPermissions.OffersRead, ct); return await data.Offers(ct); }
    public Task<AdminOffer> UpdateOffer(string? s, int id, OfferInput input, CancellationToken ct) => Write(s, AdminPermissions.OffersUpdate, "update", "offer", id.ToString(), async t => { var before = (await data.Offers(t)).SingleOrDefault(x => x.Id == id); return before is null ? (null, null, null) : (await data.SaveOffer(id, Validate(input), t), before, input); }, ct);
    public Task<AdminOffer> ApproveOffer(string? s, int id, CancellationToken ct) => Write(s, AdminPermissions.OffersApprove, "approve", "offer", id.ToString(), async t => { var before = (await data.Offers(t)).SingleOrDefault(x => x.Id == id); return before is null ? (null, null, null) : (await data.ApproveOffer(id, t), before, new { isActive = true, isOutlier = false }); }, ct);

    public async Task<IReadOnlyList<AdminMatching>> MatchingList(string? s, CancellationToken ct) { await Authorize(s, AdminPermissions.MatchingRead, ct); return await data.Matching(ct); }
    public Task<AdminMatching> ApproveMatching(string? s, int id, int productId, CancellationToken ct) => Write(s, AdminPermissions.MatchingApprove, "approve", "matching", id.ToString(), async t =>
    {
        var before = (await data.Matching(t)).SingleOrDefault(x => x.Id == id);
        if (before is null) return (null, null, null);
        var matching = await data.MatchingForApproval(id, productId, t);
        if (matching is null || !matching.TryApprove(productId)) return (null, null, null);
        await data.SaveMatching(matching, t);
        return (new AdminMatching(matching.Id, matching.Name, matching.Brand, matching.ProductId, matching.CreatedAtUtc, matching.IsApproved), before, new { productId });
    }, ct);
    public async Task<Page<AdminAuditEntry>> AuditLogs(string? s, int p, int z, CancellationToken ct) { await Authorize(s, AdminPermissions.AuditRead, ct); ValidatePage(p, z); return await data.AuditLogs(p, z, ct); }
    public async Task<AdminDashboard> Dashboard(string? s, CancellationToken ct) { await Authorize(s, AdminPermissions.DashboardRead, ct); return await data.Dashboard(ct); }
    public async Task<Page<AdminPriceAlert>> PriceAlerts(string? s, int p, int z, int? product, int? user, CancellationToken ct) { await Authorize(s, AdminPermissions.PriceAlertsReadAll, ct); ValidatePage(p, z); if (product is <= 0 || user is <= 0) throw new ArgumentException("Filter IDs must be positive."); return await data.PriceAlerts(p, z, product, user, ct); }
    public async Task<AdminAnalytics> Analytics(string? s, CancellationToken ct) { await Authorize(s, AdminPermissions.AnalyticsRead, ct); return await data.Analytics(ct); }
    public async Task<AdminProduct?> SeoProduct(string? s, int id, CancellationToken ct) { await Authorize(s, AdminPermissions.SeoRead, ct); return await data.SeoProduct(id, ct); }
    public async Task CheckSeoUnavailable(string? s, bool write, CancellationToken ct) { await Authorize(s, write ? AdminPermissions.SeoUpdate : AdminPermissions.SeoRead, ct); }
    public Task<AdminProduct> SaveSeoProduct(string? s, int id, SeoInput input, CancellationToken ct) => Write(s, AdminPermissions.SeoUpdate, "update", "product_seo", id.ToString(), async t => { if (input.Title?.Length > 200 || input.Description?.Length > 500) throw new ArgumentException("SEO values exceed their maximum length."); var before = await data.SeoProduct(id, t); return before is null ? (null, null, null) : (await data.SaveSeoProduct(id, input, t), before, input); }, ct);
    public async Task<Page<AdminUser>> Users(string? s, int p, int z, CancellationToken ct) { await Authorize(s, AdminPermissions.UsersManage, ct); ValidatePage(p, z); return await data.Users(p, z, ct); }
    public async Task<IReadOnlyList<string>> ReplaceRoles(string? s, int id, IReadOnlyList<int>? roleIds, CancellationToken ct)
    {
        var actor = await Authorize(s, AdminPermissions.UsersManage, ct);
        if (roleIds is null || roleIds.Any(x => x <= 0) || roleIds.Distinct().Count() != roleIds.Count) throw new ArgumentException("Role IDs must be positive and unique.");
        var snapshot = await data.RoleAssignmentSnapshot(id, roleIds, ct);
        if (snapshot is null) throw new KeyNotFoundException();
        var assignment = new DomainRoleAssignment(snapshot.Subject, snapshot.CurrentPermissionKeys);
        if (!assignment.CanBeReplacedBy(actor, snapshot.RequestedPermissionKeys)) throw new AdminForbiddenException();
        var roles = await data.ReplaceUserRoles(actor, id, roleIds, snapshot.CurrentRoleIds, ct);
        if (roles is null) throw new AdminForbiddenException();
        return roles;
    }
    public async Task<IReadOnlyList<AdminFeatureFlag>> FeatureFlags(string? s, CancellationToken ct) { await Authorize(s, AdminPermissions.FeatureFlagsRead, ct); return await data.FeatureFlags(ct); }
    public Task<AdminFeatureFlag> SaveFeatureFlag(string? s, string key, FeatureFlagInput input, CancellationToken ct) => Write(s, AdminPermissions.FeatureFlagsUpdate, "update", "feature_flag", key, async t =>
    {
        var featureFlag = new DomainAdminFeatureFlag(key, input.Enabled, input.Description);
        var before = (await data.FeatureFlags(t)).SingleOrDefault(x => x.Key == featureFlag.Key);
        return (await data.SaveFeatureFlag(featureFlag.Key, new(featureFlag.Enabled, featureFlag.Description), t), before, featureFlag);
    }, ct);
    public async Task<IReadOnlyList<AdminSetting>> Settings(string? s, CancellationToken ct) { await Authorize(s, AdminPermissions.SettingsManage, ct); return await data.Settings(ct); }
    public async Task<IReadOnlyList<AdminSetting>> SaveSettings(string? s, IReadOnlyDictionary<string, string>? settings, CancellationToken ct)
    {
        var actor = await Authorize(s, AdminPermissions.SettingsManage, ct);
        var validatedSettings = DomainAdminSetting.ValidateUpdates(settings);
        return await data.ExecuteAudited(actor, "update", "settings", string.Join(",", validatedSettings.Keys.Order(StringComparer.Ordinal)), async t => { var before = await data.Settings(t); var after = await data.SaveSettings(validatedSettings, t) ?? throw new KeyNotFoundException(); return (after, before, after); }, ct);
    }

    private static void ValidatePage(int page, int size) { if (page < 1 || size is < 1 or > 100) throw new ArgumentException("Page must be positive and pageSize must be between 1 and 100."); }
    private static CategoryInput Validate(CategoryInput x) { if (string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 200 || string.IsNullOrWhiteSpace(x.Slug) || x.Slug.Trim().Length > 160) throw new ArgumentException("Invalid category."); return x with { Name = x.Name.Trim(), Slug = x.Slug.Trim() }; }
    private static ShopInput Validate(ShopInput x) { if (string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 200 || string.IsNullOrWhiteSpace(x.Slug) || x.Slug.Trim().Length > 160 || x.WebsiteUrl?.Length > 2048) throw new ArgumentException("Invalid shop."); return x with { Name = x.Name.Trim(), Slug = x.Slug.Trim() }; }
    private static ProductInput Validate(ProductInput x) { if (string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 300 || string.IsNullOrWhiteSpace(x.Slug) || x.Slug.Trim().Length > 200 || x.CategoryId <= 0 || x.Brand?.Length > 200) throw new ArgumentException("Invalid product."); return x with { Name = x.Name.Trim(), Slug = x.Slug.Trim() }; }
    private static OfferInput Validate(OfferInput x) { if (x.PriceVnd <= 0 || x.Url?.Length > 2048) throw new ArgumentException("Invalid offer."); return x; }
}
