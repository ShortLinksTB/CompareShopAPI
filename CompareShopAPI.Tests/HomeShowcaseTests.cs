using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Catalog;
using CompareShopAPI.Application.HomeShowcase;
using CompareShopAPI.Domain.HomeShowcase;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class HomeShowcaseTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Slot_rules_require_exactly_fifteen_unique_positive_product_ids()
    {
        Assert.Equal(15, HomeShowcaseRules.ValidateSlots(new int?[15], "featured").Count);
        Assert.Throws<ArgumentException>(() => HomeShowcaseRules.ValidateSlots(new int?[14], "featured"));
        Assert.Throws<ArgumentException>(() => HomeShowcaseRules.ValidateSlots([1, 1, .. new int?[13]], "featured"));
        Assert.Throws<ArgumentException>(() => HomeShowcaseRules.ValidateSlots([0, .. new int?[14]], "featured"));
    }

    [Fact]
    public async Task Draft_replacement_is_atomic_and_rejects_computer_assignment_when_category_lookup_is_unavailable()
    {
        var store = new FakeStore { CategoryAvailable = false };
        store.ProductsById[7] = Product(7, "computers");
        var service = Service(store);
        var input = new HomeShowcaseDraftInput(Records(new int?[15]), Records([7, .. new int?[14]]));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReplaceDraft("admin", input, default));
        Assert.Equal(0, store.ReplaceCalls);
    }

    [Fact]
    public async Task Draft_replacement_saves_complete_snapshots_and_admin_response_reports_slot_diff()
    {
        var store = new FakeStore();
        store.ProductsById[7] = Product(7, "computers");
        var service = Service(store);
        var input = new HomeShowcaseDraftInput(Records([7, .. new int?[14]]), Records([7, .. new int?[14]]));

        var result = await service.ReplaceDraft("admin", input, default);

        Assert.Equal(1, store.ReplaceCalls);
        Assert.Equal(15, store.LastFeatured!.Count);
        Assert.Equal(15, store.LastInterested!.Count);
        Assert.Equal(2, result.ChangedSlotCount);
        Assert.Equal(2, result.Changes.Count);
    }

    [Fact]
    public async Task Public_response_uses_legacy_fallback_when_section_has_no_published_assignments()
    {
        var store = new FakeStore { Fallback = [new HomeShowcaseProduct(3, "legacy", "Legacy", null, null, new(1, "computers", "Computers", null, 1), new(null, null, 0))] };
        var result = await Service(store).Public(default);

        Assert.Equal("legacy", Assert.Single(result.InterestedComputers).Slug);
        Assert.Equal("legacy", Assert.Single(result.Featured).Slug);
    }

    [Fact]
    public async Task Read_requires_the_separate_home_showcase_read_permission()
    {
        var permissions = new FakePermissions();
        await Assert.ThrowsAsync<AdminForbiddenException>(() => Service(new FakeStore(), permissions).GetAdmin("admin", default));
    }

    private static HomeShowcaseProductData Product(int id, string slug) => new(id, slug, slug, null, null,
        new(1, slug, slug, null, 1), new(null, null, 0), true);
    private static HomeShowcaseDraftSlotInput[] Records(int?[] ids) => ids.Select((id, index) => new HomeShowcaseDraftSlotInput(index + 1, id)).ToArray();
    private static HomeShowcaseService Service(FakeStore store, FakePermissions? permissions = null) =>
        new(store, new FakeAudit(), permissions ?? new FakePermissions(AdminPermissions.HomeShowcaseRead, AdminPermissions.HomeShowcaseUpdate, AdminPermissions.HomeShowcasePublish), new TestClock(Now));

    private sealed class TestClock(DateTime value) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(value); }
    private sealed class FakePermissions(params string[] grants) : IAdminPermissionReader
    {
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => Task.FromResult(grants.Contains(permission, StringComparer.Ordinal));
    }
    private sealed class FakeAudit : IHomeShowcaseAudit
    {
        public async Task<T> Execute<T>(string actor, string action, object? before, object? after, Func<CancellationToken, Task<T>> operation, CancellationToken ct) => await operation(ct);
    }
    private sealed class FakeStore : IHomeShowcaseStore
    {
        private readonly List<HomeShowcaseSlot> slots = [];
        public Dictionary<int, HomeShowcaseProductData> ProductsById { get; } = [];
        public bool CategoryAvailable { get; init; } = true;
        public bool ComputersMatch { get; init; } = true;
        public IReadOnlyList<HomeShowcaseProduct> Fallback { get; init; } = [];
        public int ReplaceCalls { get; private set; }
        public IReadOnlyList<int?>? LastFeatured { get; private set; }
        public IReadOnlyList<int?>? LastInterested { get; private set; }
        public Task<IReadOnlyList<HomeShowcaseSlot>> Slots(HomeShowcaseSnapshot snapshot, CancellationToken ct) => Task.FromResult<IReadOnlyList<HomeShowcaseSlot>>(slots.Where(slot => slot.Snapshot == snapshot).ToArray());
        public Task<IReadOnlyDictionary<int, HomeShowcaseProductData>> Products(IReadOnlyCollection<int> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<int, HomeShowcaseProductData>>(ids.Where(ProductsById.ContainsKey).Distinct().ToDictionary(id => id, id => ProductsById[id]));
        public Task<bool> IsComputersCategoryAvailable(CancellationToken ct) => Task.FromResult(CategoryAvailable);
        public Task<bool> AreComputers(IReadOnlyCollection<int> productIds, CancellationToken ct) => Task.FromResult(ComputersMatch);
        public Task ReplaceSnapshot(HomeShowcaseSnapshot snapshot, IReadOnlyList<int?> featured, IReadOnlyList<int?> interested, CancellationToken ct)
        {
            ReplaceCalls++;
            LastFeatured = featured.ToArray(); LastInterested = interested.ToArray();
            slots.RemoveAll(slot => slot.Snapshot == snapshot);
            Add(HomeShowcaseSection.Featured, featured); Add(HomeShowcaseSection.InterestedComputers, interested);
            return Task.CompletedTask;
            void Add(HomeShowcaseSection section, IReadOnlyList<int?> ids)
            {
                for (var i = 0; i < ids.Count; i++) slots.Add(new() { Snapshot = snapshot, Section = section, SlotIndex = i + 1, ProductId = ids[i] });
            }
        }
        public Task<IReadOnlyList<HomeShowcaseProduct>> PublicProducts(HomeShowcaseSection section, CancellationToken ct) => Task.FromResult<IReadOnlyList<HomeShowcaseProduct>>([]);
        public Task<IReadOnlyList<HomeShowcaseProduct>> LegacyFallback(HomeShowcaseSection section, CancellationToken ct) => Task.FromResult(Fallback);
        public Task<int> PreviewTokensCreatedBy(string actor, DateTime sinceUtc, CancellationToken ct) => Task.FromResult(0);
        public Task SavePreviewToken(HomeShowcasePreviewToken token, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> ConsumePreviewToken(string tokenHash, DateTime nowUtc, CancellationToken ct) => Task.FromResult(false);
    }
}
