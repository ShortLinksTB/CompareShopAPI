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
    public const string OffersRead = "offers.read", OffersCreate = "offers.create", OffersUpdate = "offers.update", OffersApprove = "offers.approve";
    public const string MatchingRead = "matching.read", MatchingApprove = "matching.approve";
    public const string AuditRead = "audit_logs.read", DashboardRead = "dashboard.read", PriceAlertsReadAll = "price_alerts.read_all", AnalyticsRead = "analytics.read";
    public const string SeoRead = "seo.read", SeoUpdate = "seo.update", UsersManage = "users.manage", RolesManage = "roles.manage";
    public const string FeatureFlagsRead = "feature_flags.read", FeatureFlagsUpdate = "feature_flags.update", SettingsManage = "settings.manage";
    public const string ComplianceRead = "compliance.read", ComplianceManage = "compliance.manage";
    public const string FilesRead = "files.read", FilesWrite = "files.write";
}

public sealed record AdminCategory(int Id, string Slug, string Name, bool IsActive, int? ParentCategoryId);
public sealed record AdminShop(int Id, string Slug, string Name, string? WebsiteUrl, bool IsActive);
public sealed record AdminProduct(int Id, string Slug, string Name, string? Brand, string? Description, string? SeoTitle, string? SeoDescription, int CategoryId, bool IsActive, string? ImageUrl = null)
{
    public bool NeedsStandardImage { get; init; }
}
public sealed record AdminOffer(int Id, int ProductId, int PartnerShopId, int PriceVnd, string? Url, bool IsActive, bool IsOutlier, DateTime UpdatedAtUtc, string? OutlierReason = null);
public sealed record AdminOfferQuery(int Page = 1, int PageSize = 25, string? Q = null, int? ShopId = null, int? ProductId = null, string? Filter = null, string Sort = "attention", int StaleHours = 72);
public sealed record AdminOfferListItem(int Id, int ProductId, int PartnerShopId, int PriceVnd, string? Url, bool IsActive, bool IsOutlier, DateTime UpdatedAtUtc, string? ProductName, string? ProductImageUrl, string? PartnerShopName, string? ExternalSku, string? ImageUrl, int? OriginalPriceVnd, string StockStatus, int? LowestPriceVnd, int? DeltaVnd, decimal? DeltaPercent, string? OutlierReason, decimal? OtherOffersAveragePriceVnd, decimal? OutlierDeviationPercent);
public sealed record AdminOfferCounts(int All, int Outlier, int Stale, int OutOfStock, int Inactive);
public sealed record AdminOfferPage(IReadOnlyList<AdminOfferListItem> Items, int PageNumber, int PageSize, int TotalItems, AdminOfferCounts Counts);
public sealed record AdminOfferHistoryPoint(DateTime DateUtc, int PriceVnd);
public sealed record OfferImportInput(int ProductId, int PartnerShopId, int PriceVnd, int? OriginalPriceVnd, string SourceUrl, DateTime SourceScrapedAtUtc, string? ImageUrl, int DataSourceId, string ExternalSku);
public sealed record ImportedOffer(int Id, int ProductId, int PartnerShopId, int PriceVnd, int? OriginalPriceVnd, string SourceUrl, DateTime SourceScrapedAtUtc, string? ImageUrl);
public sealed record OfferImportResult(ImportedOffer Offer, bool Created);
public sealed record AdminMatching(int Id, string Name, string? Brand, int? ProductId, DateTime CreatedAtUtc, bool IsApproved, string? ExternalSku = null, int? DataSourceId = null)
{
    public IReadOnlyList<AdminMatchingCandidate> Candidates { get; init; } = [];
    public LaptopAttributes Attributes { get; init; } = new(null, null, null, null, null, null, null, null);
    public string Verdict { get; init; } = "review";
    public string? GroupKey { get; init; }
    public string? Source { get; init; }
    public AdminMatchingSourceDetail SourceDetail { get; init; } = new();
    public string Status { get; init; } = "pending";
    public bool NeedsStandardImage { get; init; }
}
public sealed record AdminMatchingSourceDetail(string? SpecificationsJson = null, string? ImageUrl = null,
    int? PriceVnd = null, int? OriginalPriceVnd = null, int? DataSourceId = null,
    string? DataSourceDisplayName = null, int? PartnerShopId = null, string? PartnerShopDisplayName = null);
public sealed record AdminAttachedMatching(int Id, string Name, string? Brand, int? ProductId, DateTime CreatedAtUtc, bool IsApproved, string? ExternalSku, int? DataSourceId, string? DataSourceDisplayName);
public sealed record MatchingCandidateInput(string Name, string? Brand, string? SpecificationsJson = null);
public sealed record MatchingCandidateProduct(int ProductId, string Name, string? Brand, LaptopAttributes? Attributes = null);
public sealed record AdminMatchingCandidate(int ProductId, string Name, string? Brand, double SimilarityScore)
{
    public double Score => SimilarityScore;
    public string Level { get; init; } = "review";
    public IReadOnlyList<string> Differences { get; init; } = [];
}
public sealed record AdminMatchingCounts(int All, int Pending, int Skipped, int Confident, int Review, int LikelyNew, int LikelySkip);
public sealed record AdminMatchingPage(IReadOnlyList<AdminMatching> Items, int Page, int PageSize, int TotalItems, AdminMatchingCounts Counts);
public sealed record AdminMatchingQuery(string? Status = null, string? Verdict = null, string? Q = null, int Page = 1, int PageSize = 25);
public sealed record MatchingProductCreate(string Name, string? Brand, string? Description, int CategoryId, string? ImageUrl, string ImageChoice, IReadOnlyList<int>? ApplyToIds = null, string? Slug = null);
public sealed record ProductSlugAvailability(string Slug, bool Exists);
public sealed record AdminAuditEntry(long Id, string Actor, string Action, string ResourceType, string? ResourceId, DateTime CreatedAtUtc, JsonElement? Before, JsonElement? After);
public sealed record AdminDashboard(int Products, int Offers, int Articles, bool ArticlesAvailable);
public sealed record AdminPriceAlert(int Id, int UserId, int ProductId, string ProductSlug, string ProductName, int TargetPrice, bool IsActive, DateTime CreatedAtUtc);
public sealed record AdminAnalytics(long Clicks, long Conversions, decimal Revenue);
public sealed record AdminUser(int Id, DateTime CreatedAtUtc, IReadOnlyList<string> Roles)
{
    public string KeycloakSubject { get; init; } = string.Empty;
}
public sealed record AdminFeatureFlag(int Id, string Key, bool Enabled, string? Description);
public sealed record AdminSetting(string Key, string Value);
public sealed record PermissionsResponse(IReadOnlyList<string> Permissions);
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalItems);
public sealed record CategoryInput(string Slug, string Name, bool IsActive = true, int? ParentCategoryId = null, bool ClearParentCategory = false);
public sealed record ShopInput(string Slug, string Name, string? WebsiteUrl, bool IsActive = true);
public sealed record ProductInput(string Slug, string Name, string? Brand, string? Description, int CategoryId, bool IsActive = true, string? ImageUrl = null);
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
    Task<IReadOnlyList<string>> PermissionsForSubject(string subject, CancellationToken ct);
    Task<bool> BootstrapFirstAdmin(string subject, CancellationToken ct);
    Task<Page<AdminCategory>> Categories(int page, int pageSize, CancellationToken ct);
    Task<AdminCategory?> Category(int id, CancellationToken ct);
    Task<AdminCategory> SaveCategory(int? id, CategoryInput input, CancellationToken ct);
    Task<bool> DeleteCategory(int id, CancellationToken ct);
    Task<Page<AdminShop>> Shops(int page, int pageSize, CancellationToken ct);
    Task<AdminShop?> Shop(int id, CancellationToken ct);
    Task<AdminShop> SaveShop(int? id, ShopInput input, CancellationToken ct);
    Task<bool> DeleteShop(int id, CancellationToken ct);
    Task<Page<AdminProduct>> Products(int page, int pageSize, bool? isActive, CancellationToken ct);
    Task<AdminProduct?> Product(int id, CancellationToken ct);
    Task<AdminProduct> SaveProduct(int? id, ProductInput input, CancellationToken ct);
    Task<bool> DeleteProduct(int id, CancellationToken ct);
    Task<AdminOfferPage> Offers(AdminOfferQuery query, DateTime nowUtc, CancellationToken ct);
    Task<IReadOnlyList<AdminOfferHistoryPoint>?> OfferHistory(int id, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<AdminOffer?> Offer(int id, CancellationToken ct);
    Task<AdminOffer?> SaveOffer(int id, OfferInput input, CancellationToken ct);
    Task<AdminOffer?> SaveOfferOutlierReason(int id, string? reason, CancellationToken ct);
    Task<AdminOffer?> ApproveOffer(int id, CancellationToken ct);
    Task<OfferImportResult> ImportOffer(string actor, OfferImportInput input, CancellationToken ct);
    Task<IReadOnlyList<AdminMatching>> Matching(CancellationToken ct);
    Task<IReadOnlyList<AdminMatching>> MatchingQueue(CancellationToken ct);
    Task<IReadOnlyList<AdminAttachedMatching>> MatchingsForProduct(int productId, CancellationToken ct);
    Task<DomainProductMatching?> MatchingForUnlink(int id, CancellationToken ct);
    Task<IReadOnlyList<MatchingCandidateProduct>> MatchingCandidateProducts(IReadOnlyList<MatchingCandidateInput> pending, CancellationToken ct);
    Task<DomainProductMatching?> MatchingForApproval(int id, int productId, CancellationToken ct);
    Task<IReadOnlyList<DomainProductMatching>> MatchingsForApproval(IReadOnlyList<int> ids, CancellationToken ct);
    Task<bool> ProductSlugExists(string slug, CancellationToken ct);
    Task<bool> IsLaptopCategory(int categoryId, CancellationToken ct);
    Task SaveMatching(DomainProductMatching matching, CancellationToken ct);
    Task SaveMatchingDecision(int matchingId, string decision, string? reason, string? note, string actor, DateTime atUtc, CancellationToken ct);
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

public interface IAdminPermissionReader { Task<bool> HasPermission(string subject, string permission, CancellationToken ct); }

public sealed class AdminForbiddenException() : Exception("The caller does not have permission for this operation.");
public sealed class AdminUnauthorizedException() : Exception("A validated subject is required.");
public sealed class AdminBootstrapCompletedException() : Exception("The first admin has already been provisioned.");
public sealed class ProductDeleteConflictException() : Exception("The product has linked records and cannot be deleted.");
public sealed class AdminOfferImportConflictException() : Exception("The source item is already mapped to another product.");
public sealed class MatchingLaptopCategoryUnavailableException() : Exception("A configured Laptop category is unavailable.");
public sealed class MatchingProductSlugConflictException(string slug) : Exception($"Product slug '{slug}' is already in use.") { public string Slug { get; } = slug; }

public sealed class AdminService(IAdminData data, IReadOnlySet<string>? bootstrapSubjects = null, TimeProvider? clock = null)
{
    private TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    private async Task<string> Authorize(string? subject, string permission, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        if (!await data.HasPermission(subject, permission, ct)) throw new AdminForbiddenException();
        return subject;
    }

    public async Task<PermissionsResponse> MyPermissions(string? subject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AdminUnauthorizedException();
        return new PermissionsResponse(await data.PermissionsForSubject(subject, ct));
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

    public async Task<Page<AdminProduct>> Products(string? subject, int page, int size, bool? isActive, CancellationToken ct) { await Authorize(subject, AdminPermissions.ProductsRead, ct); ValidatePage(page, size); return await data.Products(page, size, isActive, ct); }
    public async Task<ProductSlugAvailability> ProductSlugAvailability(string? subject, string? slug, CancellationToken ct)
    {
        var actor = await Authorize(subject, AdminPermissions.MatchingApprove, ct);
        if (!await data.HasPermission(actor, AdminPermissions.ProductsCreate, ct)) throw new AdminForbiddenException();
        var canonicalSlug = CanonicalizeSlug(slug ?? string.Empty);
        if (string.IsNullOrWhiteSpace(canonicalSlug) || canonicalSlug.Length > 200) throw new ArgumentException("Slug must produce a valid product slug.");
        return new(canonicalSlug, await data.ProductSlugExists(canonicalSlug, ct));
    }
    public async Task<AdminProduct?> Product(string? subject, int id, CancellationToken ct) { await Authorize(subject, AdminPermissions.ProductsRead, ct); return await data.Product(id, ct); }
    public Task<AdminProduct> CreateProduct(string? s, ProductInput i, CancellationToken ct) => Write<AdminProduct>(s, AdminPermissions.ProductsCreate, "create", "product", null, async t => (await data.SaveProduct(null, Validate(i), t), null, i), ct);
    public Task<AdminProduct> UpdateProduct(string? s, int id, ProductInput i, CancellationToken ct) => Write(s, AdminPermissions.ProductsUpdate, "update", "product", id.ToString(), async t => { var before = await data.Product(id, t); return before is null ? (null, null, null) : (await data.SaveProduct(id, Validate(i), t), before, i); }, ct);
    public async Task DeleteProduct(string? s, int id, CancellationToken ct) { var actor = await Authorize(s, AdminPermissions.ProductsDelete, ct); await data.ExecuteAudited<bool>(actor, "delete", "product", id.ToString(), async t => { var old = await data.Product(id, t); if (old is null || !await data.DeleteProduct(id, t)) throw new KeyNotFoundException(); return (true, old, null); }, ct); }

    public async Task<AdminOfferPage> Offers(string? s, AdminOfferQuery query, CancellationToken ct)
    {
        await Authorize(s, AdminPermissions.OffersRead, ct);
        ValidateOfferQuery(query);
        return await data.Offers(query, Clock.GetUtcNow().UtcDateTime, ct);
    }
    public async Task<IReadOnlyList<AdminOfferHistoryPoint>?> OfferHistory(string? s, int id, int days, CancellationToken ct)
    {
        await Authorize(s, AdminPermissions.OffersRead, ct);
        if (days is < 1 or > 90) throw new ArgumentException("days must be between 1 and 90.");
        var now = Clock.GetUtcNow().UtcDateTime;
        return await data.OfferHistory(id, now.AddDays(-days), now, ct);
    }
    public async Task<OfferImportResult> ImportOffer(string? subject, OfferImportInput input, CancellationToken ct)
    {
        var actor = await Authorize(subject, AdminPermissions.OffersCreate, ct);
        if (input.ProductId <= 0 || input.PartnerShopId <= 0 || input.DataSourceId <= 0 || input.PriceVnd <= 0 || input.OriginalPriceVnd is <= 0 || input.OriginalPriceVnd is int original && original < input.PriceVnd ||
            input.SourceScrapedAtUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(input.ExternalSku) || input.ExternalSku.Length > 160 ||
            !IsValidHttpsUrl(input.SourceUrl, 2048) || input.ImageUrl is not null && !IsValidHttpsUrl(input.ImageUrl, 2048))
            throw new ArgumentException("Invalid offer import values.");
        return await data.ImportOffer(actor, input with { ExternalSku = input.ExternalSku.Trim() }, ct);
    }
    public Task<AdminOffer> UpdateOffer(string? s, int id, OfferInput input, CancellationToken ct) => Write(s, AdminPermissions.OffersUpdate, "update", "offer", id.ToString(), async t => { var before = await data.Offer(id, t); var validated = Validate(input); return before is null ? (null, null, null) : (await data.SaveOffer(id, validated, t), before, validated); }, ct);
    public Task<AdminOffer> UpdateOfferOutlierReason(string? s, int id, string? reason, CancellationToken ct)
    {
        return Write(s, AdminPermissions.OffersUpdate, "update", "offer", id.ToString(), async t =>
        {
            if (reason?.Trim().Length > 500) throw new ArgumentException("OutlierReason must be at most 500 characters.");
            var normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            var before = await data.Offer(id, t);
            return before is null ? (null, null, null) : (await data.SaveOfferOutlierReason(id, normalized, t), before, new { outlierReason = normalized });
        }, ct);
    }
    public Task<AdminOffer> ApproveOffer(string? s, int id, CancellationToken ct) => Write(s, AdminPermissions.OffersApprove, "approve", "offer", id.ToString(), async t => { var before = await data.Offer(id, t); return before is null ? (null, null, null) : (await data.ApproveOffer(id, t), before, new { isActive = true, isOutlier = false }); }, ct);

    public async Task<IReadOnlyList<AdminMatching>> MatchingList(string? s, CancellationToken ct)
    {
        await Authorize(s, AdminPermissions.MatchingRead, ct);
        var pending = await data.Matching(ct);
        if (pending.Count == 0) return pending;
        var products = await data.MatchingCandidateProducts(pending.Select(x => new MatchingCandidateInput(x.Name, x.Brand)).ToArray(), ct);
        var ranker = new MatchingCandidateRanker(products);
        return pending.Select(x =>
        {
            var candidates = ranker.Rank(x.Attributes);
            var verdict = MatchingCandidateRanker.Verdict(x.Attributes, candidates);
            candidates = MatchingCandidateRanker.ApplyVerdictGuard(verdict, candidates);
            return x with { Candidates = candidates, Verdict = verdict };
        }).ToArray();
    }
    public async Task<AdminMatchingPage> MatchingPage(string? s, AdminMatchingQuery query, CancellationToken ct)
    {
        await Authorize(s, AdminPermissions.MatchingRead, ct);
        ValidatePage(query.Page, query.PageSize);
        if (query.Status is not (null or "pending" or "skipped" or "approved") || query.Verdict is not (null or "confident" or "review" or "likely_new" or "likely_skip")) throw new ArgumentException("Invalid matching status or verdict.");
        var all = await MatchingList(s, ct);
        var skipped = (await data.MatchingQueue(ct)).Where(x => x.Status == "skipped").ToArray();
        var queue = all.Concat(skipped).ToArray();
        var counts = new AdminMatchingCounts(queue.Length, all.Count, skipped.Length,
            all.Count(x => x.Verdict == "confident"), all.Count(x => x.Verdict == "review"), all.Count(x => x.Verdict == "likely_new"), all.Count(x => x.Verdict == "likely_skip"));
        var filtered = queue.AsEnumerable();
        if (query.Status is not null) filtered = filtered.Where(x => x.Status == query.Status);
        if (query.Verdict is not null) filtered = filtered.Where(x => x.Verdict == query.Verdict);
        if (!string.IsNullOrWhiteSpace(query.Q)) filtered = filtered.Where(x => (x.Name + " " + x.Brand + " " + x.ExternalSku + " " + x.Source).Contains(query.Q.Trim(), StringComparison.OrdinalIgnoreCase));
        var rows = filtered.ToArray();
        return new(rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArray(), query.Page, query.PageSize, rows.Length, counts);
    }
    public Task<AdminMatching> ApproveMatching(string? s, int id, int productId, CancellationToken ct) => Write(s, AdminPermissions.MatchingApprove, "approve", "matching", id.ToString(), async t =>
    {
        var before = (await data.Matching(t)).SingleOrDefault(x => x.Id == id);
        if (before is null) return (null, null, null);
        var matching = await data.MatchingForApproval(id, productId, t);
        if (matching is null || !matching.TryApprove(productId)) return (null, null, null);
        await data.SaveMatching(matching, t);
        await data.SaveMatchingDecision(id, "approved", null, null, s!, Clock.GetUtcNow().UtcDateTime, t);
        return (new AdminMatching(matching.Id, matching.Name, matching.Brand, matching.ProductId, matching.CreatedAtUtc, matching.IsApproved, matching.ExternalSku, matching.DataSourceId) { Status = matching.Status }, before, new { productId });
    }, ct);
    public async Task<AdminMatching> SkipMatching(string? s, int id, string reason, string? note, CancellationToken ct)
    {
        var actor = await Authorize(s, AdminPermissions.MatchingApprove, ct);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 80 || note?.Trim().Length > 1000) throw new ArgumentException("Invalid skip reason or note.");
        return await data.ExecuteAudited(actor, "skip", "matching", id.ToString(), async t =>
        {
            var matching = await data.MatchingForUnlink(id, t);
            if (matching is null) return (null, null, null);
            var before = matching.Status;
            matching.Skip(reason.Trim(), note?.Trim(), actor, Clock.GetUtcNow().UtcDateTime);
            await data.SaveMatching(matching, t);
            await data.SaveMatchingDecision(id, "skipped", reason.Trim(), note?.Trim(), actor, Clock.GetUtcNow().UtcDateTime, t);
            return (new AdminMatching(matching.Id, matching.Name, matching.Brand, matching.ProductId, matching.CreatedAtUtc, matching.IsApproved, matching.ExternalSku, matching.DataSourceId) { Status = matching.Status }, new { status = before }, new { status = matching.Status, reason, note });
        }, ct);
    }
    public async Task<AdminMatching> RestoreMatching(string? s, int id, CancellationToken ct)
    {
        var actor = await Authorize(s, AdminPermissions.MatchingApprove, ct);
        return await data.ExecuteAudited(actor, "restore", "matching", id.ToString(), async t =>
        {
            var matching = await data.MatchingForUnlink(id, t);
            if (matching is null) return (null, null, null);
            if (matching.Status != "skipped") return (null, null, null);
            matching.Restore();
            await data.SaveMatching(matching, t);
            await data.SaveMatchingDecision(id, "restored", null, null, actor, Clock.GetUtcNow().UtcDateTime, t);
            return (new AdminMatching(matching.Id, matching.Name, matching.Brand, matching.ProductId, matching.CreatedAtUtc, matching.IsApproved, matching.ExternalSku, matching.DataSourceId) { Status = matching.Status }, new { status = "skipped" }, new { status = "pending" });
        }, ct);
    }
    public async Task<IReadOnlyList<AdminMatching>> ApproveMatchingGroup(string? s, IReadOnlyList<int> ids, int productId, CancellationToken ct)
    {
        var actor = await Authorize(s, AdminPermissions.MatchingApprove, ct);
        if (ids is null || ids.Count is < 1 or > 100 || ids.Any(x => x <= 0) || ids.Distinct().Count() != ids.Count || productId <= 0) throw new ArgumentException("Matching IDs and product ID must be positive and unique.");
        if (await data.Product(productId, ct) is not { IsActive: true }) throw new ArgumentException("Active product does not exist.");
        return await data.ExecuteAudited(actor, "approve_group", "matching_group", null, async t =>
        {
            var matchings = await data.MatchingsForApproval(ids, t);
            if (matchings.Count != ids.Count) return (null, null, null);
            var before = matchings.Select(x => new { x.Id, x.ProductId, x.Status }).ToArray();
            foreach (var matching in matchings)
            {
                if (!matching.TryApprove(productId)) throw new ArgumentException("Every matching must be pending before group approval.");
                await data.SaveMatching(matching, t);
                await data.SaveMatchingDecision(matching.Id, "approved", null, null, actor, Clock.GetUtcNow().UtcDateTime, t);
            }
            var result = matchings.Select(x => new AdminMatching(x.Id, x.Name, x.Brand, x.ProductId, x.CreatedAtUtc, x.IsApproved, x.ExternalSku, x.DataSourceId) { Status = x.Status }).ToArray();
            return (result, before, new { productId, ids });
        }, ct);
    }
    public async Task<AdminProduct> CreateMatchingProduct(string? s, int id, MatchingProductCreate input, CancellationToken ct)
    {
        var actor = await Authorize(s, AdminPermissions.MatchingApprove, ct);
        if (!await data.HasPermission(actor, AdminPermissions.ProductsCreate, ct)) throw new AdminForbiddenException();
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 300 || input.Brand?.Length > 200 || input.Description?.Length > 10000 || input.CategoryId <= 0 || input.ImageChoice is not ("files" or "shop" or "none")) throw new ArgumentException("Invalid matching product fields.");
        var ids = input.ApplyToIds?.ToArray() ?? [id];
        if (!ids.Contains(id) || ids.Length is < 1 or > 100 || ids.Any(x => x <= 0) || ids.Distinct().Count() != ids.Length) throw new ArgumentException("Apply-to matching IDs must be unique and include the source item.");
        var slug = CanonicalizeSlug(input.Slug ?? input.Name);
        if (string.IsNullOrWhiteSpace(slug) || slug.Length > 200) throw new ArgumentException("Product slug cannot produce a valid slug.");
        if (!await data.IsLaptopCategory(input.CategoryId, ct)) throw new MatchingLaptopCategoryUnavailableException();
        if (await data.ProductSlugExists(slug, ct)) throw new MatchingProductSlugConflictException(slug);
        var imageUrl = input.ImageChoice == "none" ? null : input.ImageUrl;
        if (input.ImageChoice == "files" && imageUrl is not null && !System.Text.RegularExpressions.Regex.IsMatch(imageUrl, @"^/files/[0-9a-fA-F-]{36}$")) throw new ArgumentException("Files image must use /files/{guid}.");
        if (input.ImageChoice == "shop" && imageUrl is not null && !IsValidHttpsUrl(imageUrl, 2048)) throw new ArgumentException("Shop image URL must be HTTPS.");
        var productInput = new ProductInput(slug, input.Name.Trim(), input.Brand?.Trim(), input.Description?.Trim(), input.CategoryId, true, imageUrl);
        return await data.ExecuteAudited<AdminProduct>(actor, "create_from_matching", "product", null, async t =>
        {
            var matchings = await data.MatchingsForApproval(ids, t);
            if (matchings.Count != ids.Length) return (null, null, null);
            var product = await data.SaveProduct(null, productInput, t);
            foreach (var matching in matchings)
            {
                if (!matching.TryApprove(product.Id)) throw new ArgumentException("Every matching must be pending.");
                await data.SaveMatching(matching, t);
                await data.SaveMatchingDecision(matching.Id, "created_product", null, null, actor, Clock.GetUtcNow().UtcDateTime, t);
            }
            var needsStandardImage = input.ImageChoice != "files" || imageUrl is null;
            return (product with { NeedsStandardImage = needsStandardImage }, null, new { productId = product.Id, matchingIds = ids, input.ImageChoice, needsStandardImage });
        }, ct);
    }
    private static string CanonicalizeSlug(string value)
    {
        var folded = string.Concat(value.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.Replace(folded, "[^a-z0-9]+", "-").Trim('-');
    }
    public async Task<IReadOnlyList<AdminAttachedMatching>> ProductMatchings(string? s, int productId, CancellationToken ct)
    {
        await Authorize(s, AdminPermissions.MatchingRead, ct);
        return await data.MatchingsForProduct(productId, ct);
    }
    public async Task UnlinkMatching(string? s, int id, CancellationToken ct)
    {
        var actor = await Authorize(s, AdminPermissions.MatchingApprove, ct);
        await data.ExecuteAudited<bool?>(actor, "unlink", "matching", id.ToString(), async t =>
        {
            var matching = await data.MatchingForUnlink(id, t);
            if (matching is null) return (null, null, null);
            var before = new AdminMatching(matching.Id, matching.Name, matching.Brand, matching.ProductId, matching.CreatedAtUtc, matching.IsApproved, matching.ExternalSku, matching.DataSourceId);
            matching.UnlinkFromProduct();
            await data.SaveMatching(matching, t);
            return ((bool?)true, before, new { productId = (int?)null });
        }, ct);
    }
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
    private static void ValidateOfferQuery(AdminOfferQuery q)
    {
        ValidatePage(q.Page, q.PageSize);
        if (q.StaleHours is < 1 or > 8760 || q.ShopId is <= 0 || q.ProductId is <= 0) throw new ArgumentException("Invalid offer query range.");
        if (q.Filter is not null && q.Filter is not ("outlier" or "stale" or "out_of_stock" or "inactive")) throw new ArgumentException("Invalid offer filter.");
        if (q.Sort is not ("attention" or "oldest" or "priceAsc" or "priceDesc" or "name")) throw new ArgumentException("Invalid offer sort.");
    }
    private static bool IsValidHttpsUrl(string value, int maxLength) => value.Length <= maxLength && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrWhiteSpace(uri.UserInfo) && !value.Any(char.IsControl);
    private static CategoryInput Validate(CategoryInput x) { if (string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 200 || string.IsNullOrWhiteSpace(x.Slug) || x.Slug.Trim().Length > 160) throw new ArgumentException("Invalid category."); return x with { Name = x.Name.Trim(), Slug = x.Slug.Trim() }; }
    private static ShopInput Validate(ShopInput x) { if (string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 200 || string.IsNullOrWhiteSpace(x.Slug) || x.Slug.Trim().Length > 160 || x.WebsiteUrl?.Length > 2048) throw new ArgumentException("Invalid shop."); return x with { Name = x.Name.Trim(), Slug = x.Slug.Trim() }; }
    private static ProductInput Validate(ProductInput x)
    {
        var imageUrl = x.ImageUrl?.Trim();
        var isFileReference = imageUrl is not null && System.Text.RegularExpressions.Regex.IsMatch(imageUrl, @"^/files/[0-9a-fA-F-]{36}$");
        if (string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 300 || string.IsNullOrWhiteSpace(x.Slug) || x.Slug.Trim().Length > 200 || x.CategoryId <= 0 || x.Brand?.Length > 200 || imageUrl?.Length > 2048 || imageUrl is not null && !isFileReference && (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri) || imageUri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(imageUri.Host))) throw new ArgumentException("Invalid product.");
        return x with { Name = x.Name.Trim(), Slug = x.Slug.Trim(), ImageUrl = imageUrl };
    }
    private static OfferInput Validate(OfferInput x) { if (x.PriceVnd <= 0 || x.Url?.Length > 2048) throw new ArgumentException("Invalid offer."); return x; }
}
