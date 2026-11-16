using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Domain.HomeShowcase;
using CompareShopAPI.Application.Admin;

namespace CompareShopAPI.Application.HomeShowcase;

public sealed record HomeShowcaseProduct(int Id, string Slug, string Name, string? ImageUrl, string? Brand, CategoryDto Category, OfferSummaryDto OfferSummary);
public sealed record HomeShowcaseAdminProduct(int Id, string Slug, string Name, string? ImageUrl, string? Brand, CategoryDto Category, OfferSummaryDto OfferSummary, bool IsVisible);
public sealed record HomeShowcaseSlotDto(int SlotIndex, HomeShowcaseAdminProduct? Product);
public sealed record HomeShowcaseSectionDto(IReadOnlyList<HomeShowcaseSlotDto> Featured, IReadOnlyList<HomeShowcaseSlotDto> InterestedComputers);
public sealed record HomeShowcaseDiff(int SlotIndex, HomeShowcaseAdminProduct? Before, HomeShowcaseAdminProduct? After, string Section);
public sealed record HomeShowcaseAdminResponse(HomeShowcaseSectionDto Draft, HomeShowcaseSectionDto Published, int ChangedSlotCount, IReadOnlyList<HomeShowcaseDiff> Changes);
public sealed record HomeShowcasePublicResponse(IReadOnlyList<HomeShowcaseProduct> Featured, IReadOnlyList<HomeShowcaseProduct> InterestedComputers);
public sealed record HomeShowcaseDraftInput(IReadOnlyList<HomeShowcaseSlotInput?>? Featured, IReadOnlyList<HomeShowcaseSlotInput?>? InterestedComputers);
public sealed record HomeShowcaseProductData(int Id, string Slug, string Name, string? ImageUrl, string? Brand, CategoryDto Category, OfferSummaryDto OfferSummary, bool IsActive);

public interface IHomeShowcaseStore
{
    Task<IReadOnlyList<HomeShowcaseSlot>> Slots(HomeShowcaseSnapshot snapshot, CancellationToken ct);
    Task<IReadOnlyDictionary<int, HomeShowcaseProductData>> Products(IReadOnlyCollection<int> ids, CancellationToken ct);
    Task<bool> IsComputersCategoryAvailable(CancellationToken ct);
    Task<bool> AreComputers(IReadOnlyCollection<int> productIds, CancellationToken ct);
    Task ReplaceSnapshot(HomeShowcaseSnapshot snapshot, IReadOnlyList<int?> featured, IReadOnlyList<int?> interested, CancellationToken ct);
    Task<IReadOnlyList<HomeShowcaseProduct>> PublicProducts(HomeShowcaseSection section, CancellationToken ct);
    Task<IReadOnlyList<HomeShowcaseProduct>> LegacyFallback(HomeShowcaseSection section, CancellationToken ct);
    Task<int> PreviewTokensCreatedBy(string actor, DateTime sinceUtc, CancellationToken ct);
    Task SavePreviewToken(HomeShowcasePreviewToken token, CancellationToken ct);
    Task<bool> ConsumePreviewToken(string tokenHash, DateTime nowUtc, CancellationToken ct);
}

public interface IHomeShowcaseAudit
{
    Task<T> Execute<T>(string actor, string action, object? before, object? after, Func<CancellationToken, Task<T>> operation, CancellationToken ct);
}

public sealed class HomeShowcaseService(IHomeShowcaseStore store, IHomeShowcaseAudit audit, IAdminPermissionReader permissionReader, TimeProvider clock)
{
    private const int PreviewTokenLifetimeMinutes = 10;
    private const int PreviewTokenRateLimit = 5;
    private static readonly TimeSpan PreviewTokenRateWindow = TimeSpan.FromMinutes(1);

    public async Task<HomeShowcaseAdminResponse> GetAdmin(string? actor, CancellationToken ct)
    {
        await Authorize(actor, AdminPermissions.HomeShowcaseRead, ct);
        var draft = await BuildAdminSection(HomeShowcaseSnapshot.Draft, ct);
        var published = await BuildAdminSection(HomeShowcaseSnapshot.Published, ct);
        var changes = Diff(draft, published);
        return new(draft, published, changes.Count, changes);
    }

    public async Task<HomeShowcaseAdminResponse> ReplaceDraft(string? actor, HomeShowcaseDraftInput input, CancellationToken ct)
    {
        var subject = await Authorize(actor, AdminPermissions.HomeShowcaseUpdate, ct);
        var featured = HomeShowcaseRules.ValidateSlots(input.Featured, "featured");
        var interested = HomeShowcaseRules.ValidateSlots(input.InterestedComputers, "interestedComputers");
        var ids = featured.Concat(interested).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        IReadOnlyDictionary<int, HomeShowcaseProductData> products;
        try { products = await store.Products(ids, ct); }
        catch (Exception ex) when (ids.Length > 0 && ex is not OperationCanceledException)
        {
            throw new ArgumentException("Product lookup is unavailable; the draft was not changed.", "productIds");
        }
        if (ids.Any(id => !products.ContainsKey(id))) throw new ArgumentException("One or more selected products do not exist.", "productIds");
        var interestedIds = interested.Where(id => id.HasValue).Select(id => id!.Value).ToArray();
        if (interestedIds.Length > 0)
        {
            try
            {
                if (!await store.IsComputersCategoryAvailable(ct) || !await store.AreComputers(interestedIds, ct))
                    throw new ArgumentException("Interested-computer slots require products in the active computers category.", "interestedComputers");
            }
            catch (ArgumentException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ArgumentException("The computers category is unavailable; interested-computer assignments were rejected.", "interestedComputers");
            }
        }
        var current = await BuildAdminSection(HomeShowcaseSnapshot.Draft, ct);
        var desired = new HomeShowcaseSectionDto(ToSlotRecords(featured, products), ToSlotRecords(interested, products));
        var changed = Diff(desired, current).Count;
        return await audit.Execute(subject, "replace_draft", AuditSnapshot(current, changed), AuditSnapshot(desired, changed), async token =>
        {
            await store.ReplaceSnapshot(HomeShowcaseSnapshot.Draft, featured, interested, token);
            return await GetAdmin(subject, token);
        }, ct);
    }

    public async Task<HomeShowcaseAdminResponse> ResetDraft(string? actor, CancellationToken ct)
    {
        var subject = await Authorize(actor, AdminPermissions.HomeShowcaseUpdate, ct);
        var before = await BuildAdminSection(HomeShowcaseSnapshot.Draft, ct);
        var published = await store.Slots(HomeShowcaseSnapshot.Published, ct);
        var productMap = await store.Products(published.Where(slot => slot.ProductId.HasValue).Select(slot => slot.ProductId!.Value).Distinct().ToArray(), ct);
        var featured = Enumerable.Range(1, HomeShowcaseRules.SlotCount).Select(index => published.FirstOrDefault(slot => slot.Section == HomeShowcaseSection.Featured && slot.SlotIndex == index)?.ProductId).ToArray();
        var interested = Enumerable.Range(1, HomeShowcaseRules.SlotCount).Select(index => published.FirstOrDefault(slot => slot.Section == HomeShowcaseSection.InterestedComputers && slot.SlotIndex == index)?.ProductId).ToArray();
        var after = new HomeShowcaseSectionDto(ToSlotRecords(featured, productMap), ToSlotRecords(interested, productMap));
        var changed = Diff(after, before).Count;
        return await audit.Execute(subject, "reset_draft", AuditSnapshot(before, changed), AuditSnapshot(after, changed), async token =>
        {
            await store.ReplaceSnapshot(HomeShowcaseSnapshot.Draft, featured, interested, token);
            return await GetAdmin(subject, token);
        }, ct);
    }

    public async Task<HomeShowcaseAdminResponse> Publish(string? actor, Action invalidateCache, CancellationToken ct)
    {
        var subject = await Authorize(actor, AdminPermissions.HomeShowcasePublish, ct);
        var draft = await BuildAdminSection(HomeShowcaseSnapshot.Draft, ct);
        var published = await BuildAdminSection(HomeShowcaseSnapshot.Published, ct);
        var changes = Diff(draft, published);
        if (changes.Count > 0)
        {
            await ValidateSnapshot(draft, ct);
            var featured = Enumerable.Range(1, HomeShowcaseRules.SlotCount).Select(index => draft.Featured[index - 1].Product?.Id).ToArray();
            var interested = Enumerable.Range(1, HomeShowcaseRules.SlotCount).Select(index => draft.InterestedComputers[index - 1].Product?.Id).ToArray();
            await audit.Execute(subject, "publish", AuditSnapshot(published, changes.Count), AuditSnapshot(draft, changes.Count), async token =>
            {
                await store.ReplaceSnapshot(HomeShowcaseSnapshot.Published, featured, interested, token);
                return true;
            }, ct);
            invalidateCache();
        }
        return await GetAdmin(subject, ct);
    }

    public async Task<HomeShowcasePublicResponse> Public(CancellationToken ct) => new(
        await PublicSection(HomeShowcaseSection.Featured, ct), await PublicSection(HomeShowcaseSection.InterestedComputers, ct));

    public async Task<string> CreatePreviewToken(string? actor, CancellationToken ct)
    {
        var subject = await Authorize(actor, AdminPermissions.HomeShowcaseRead, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (await store.PreviewTokensCreatedBy(subject, now - PreviewTokenRateWindow, ct) >= PreviewTokenRateLimit)
            throw new HomeShowcaseRateLimitException();
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        await audit.Execute(subject, "preview_token", null, new { expiresAtUtc = now.AddMinutes(PreviewTokenLifetimeMinutes) }, async tokenCt =>
        {
            await store.SavePreviewToken(new HomeShowcasePreviewToken { TokenHash = hash, ActorSubject = subject, CreatedAtUtc = now, ExpiresAtUtc = now.AddMinutes(PreviewTokenLifetimeMinutes) }, tokenCt);
            return true;
        }, ct);
        return token;
    }

    public async Task<HomeShowcasePublicResponse?> Preview(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
        if (!await store.ConsumePreviewToken(hash, clock.GetUtcNow().UtcDateTime, ct)) return null;
        var draft = await store.Slots(HomeShowcaseSnapshot.Draft, ct);
        return new(await PreviewSection(HomeShowcaseSection.Featured, draft, ct), await PreviewSection(HomeShowcaseSection.InterestedComputers, draft, ct));
    }

    private async Task<IReadOnlyList<HomeShowcaseProduct>> PublicSection(HomeShowcaseSection section, CancellationToken ct)
    {
        var assigned = await store.PublicProducts(section, ct);
        return assigned.Count == 0 ? await store.LegacyFallback(section, ct) : assigned.Take(HomeShowcaseRules.SlotCount).ToArray();
    }

    private async Task<IReadOnlyList<HomeShowcaseProduct>> PreviewSection(HomeShowcaseSection section, IReadOnlyList<HomeShowcaseSlot> slots, CancellationToken ct)
    {
        var ids = slots.Where(slot => slot.Section == section && slot.ProductId.HasValue).OrderBy(slot => slot.SlotIndex).Select(slot => slot.ProductId!.Value).ToArray();
        var map = await store.Products(ids, ct);
        var selected = ids.Where(map.ContainsKey).Select(id => map[id]).Where(product => product.IsActive).Select(ToPublicProduct).ToArray();
        return selected.Length == 0 ? await store.LegacyFallback(section, ct) : selected.Take(HomeShowcaseRules.SlotCount).ToArray();
    }

    private async Task<HomeShowcaseSectionDto> BuildAdminSection(HomeShowcaseSnapshot snapshot, CancellationToken ct)
    {
        var slots = await store.Slots(snapshot, ct);
        var products = await store.Products(slots.Where(slot => slot.ProductId.HasValue).Select(slot => slot.ProductId!.Value).Distinct().ToArray(), ct);
        return new(BuildSlots(slots, HomeShowcaseSection.Featured, products), BuildSlots(slots, HomeShowcaseSection.InterestedComputers, products));
    }

    private async Task ValidateSnapshot(HomeShowcaseSectionDto snapshot, CancellationToken ct)
    {
        var ids = snapshot.Featured.Concat(snapshot.InterestedComputers).Where(slot => slot.Product is not null).Select(slot => slot.Product!.Id).Distinct().ToArray();
        IReadOnlyDictionary<int, HomeShowcaseProductData> products;
        try { products = await store.Products(ids, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new ArgumentException("Product lookup is unavailable; publish was rejected.", "productIds"); }
        if (ids.Any(id => !products.ContainsKey(id))) throw new ArgumentException("One or more selected products no longer exist.", "productIds");
        var interestedIds = snapshot.InterestedComputers.Where(slot => slot.Product is not null).Select(slot => slot.Product!.Id).ToArray();
        if (interestedIds.Length == 0) return;
        try
        {
            if (!await store.IsComputersCategoryAvailable(ct) || !await store.AreComputers(interestedIds, ct))
                throw new ArgumentException("Interested-computer slots require products in the active computers category.", "interestedComputers");
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new ArgumentException("The computers category is unavailable; publish was rejected.", "interestedComputers"); }
    }

    private static IReadOnlyList<HomeShowcaseSlotDto> BuildSlots(IReadOnlyList<HomeShowcaseSlot> rows, HomeShowcaseSection section, IReadOnlyDictionary<int, HomeShowcaseProductData> products) =>
        Enumerable.Range(1, HomeShowcaseRules.SlotCount).Select(index =>
        {
            var id = rows.FirstOrDefault(slot => slot.Section == section && slot.SlotIndex == index)?.ProductId;
            return new HomeShowcaseSlotDto(index, id.HasValue && products.TryGetValue(id.Value, out var p) ? ToAdminProduct(p) : null);
        }).ToArray();

    private static IReadOnlyList<HomeShowcaseSlotDto> ToSlotRecords(IReadOnlyList<int?> ids, IReadOnlyDictionary<int, HomeShowcaseProductData> products) =>
        Enumerable.Range(1, HomeShowcaseRules.SlotCount).Select(index => new HomeShowcaseSlotDto(index, ids[index - 1] is int id && products.TryGetValue(id, out var p) ? ToAdminProduct(p) : null)).ToArray();

    private static IReadOnlyList<HomeShowcaseDiff> Diff(HomeShowcaseSectionDto draft, HomeShowcaseSectionDto published)
    {
        var diffs = new List<HomeShowcaseDiff>();
        Add(draft.Featured, published.Featured, "featured"); Add(draft.InterestedComputers, published.InterestedComputers, "interestedComputers");
        return diffs;
        void Add(IReadOnlyList<HomeShowcaseSlotDto> current, IReadOnlyList<HomeShowcaseSlotDto> old, string section)
        {
            for (var i = 0; i < HomeShowcaseRules.SlotCount; i++)
                if (current[i].Product?.Id != old[i].Product?.Id) diffs.Add(new(i + 1, old[i].Product, current[i].Product, section));
        }
    }

    private static HomeShowcaseAdminProduct ToAdminProduct(HomeShowcaseProductData p) => new(p.Id, p.Slug, p.Name, p.ImageUrl, p.Brand, p.Category, p.OfferSummary, p.IsActive);
    private static HomeShowcaseProduct ToPublicProduct(HomeShowcaseProductData p) => new(p.Id, p.Slug, p.Name, p.ImageUrl, p.Brand, p.Category, p.OfferSummary);
    private static object AuditSnapshot(HomeShowcaseSectionDto snapshot, int changedSlotCount) => new
    {
        changedSlotCount,
        featured = snapshot.Featured.Select(slot => new { slot.SlotIndex, productId = slot.Product?.Id }),
        interestedComputers = snapshot.InterestedComputers.Select(slot => new { slot.SlotIndex, productId = slot.Product?.Id })
    };
    private static string RequireActor(string? actor) => !string.IsNullOrWhiteSpace(actor) ? actor : throw new ArgumentException("A validated admin subject is required.");
    private async Task<string> Authorize(string? actor, string permission, CancellationToken ct)
    {
        var subject = RequireActor(actor);
        if (!await permissionReader.HasPermission(subject, permission, ct)) throw new AdminForbiddenException();
        return subject;
    }
}

public sealed class HomeShowcaseRateLimitException() : Exception("Preview token issuance rate limit exceeded.");
