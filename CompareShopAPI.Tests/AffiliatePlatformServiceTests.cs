using CompareShopAPI.Application.Admin;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class AffiliatePlatformServiceTests
{
    [Fact]
    public async Task Crud_trims_outer_whitespace_preserves_internal_spacing_and_rejects_case_insensitive_duplicate()
    {
        var store = new MemoryStore();
        var service = CreateService(store);
        var created = await service.Create("writer", new("  Acme  Partner ", "https://example.test", null), default);

        Assert.Equal("Acme  Partner", created.Name);
        Assert.Equal("ACME  PARTNER", store.Rows[0].NormalizedName);
        await Assert.ThrowsAsync<AffiliatePlatformDuplicateException>(() =>
            service.Create("writer", new("acme  partner", null, null), default));
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task Preview_reports_zero_based_row_errors_and_does_not_write()
    {
        var store = new MemoryStore();
        var service = CreateService(store);
        var preview = await service.Preview("writer", [
            new(" Existing ", null, null),
            new("existing", "not a url", null),
            new("   ", null, null),
            new("New  Platform", "https://example.test", "long notes")
        ], default);

        Assert.Equal(1, preview.CreateCount);
        Assert.Equal(0, preview.UpdateCount);
        Assert.Equal(new[] { 0, 1, 2 }, preview.Errors.Select(x => x.Index));
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task Import_commit_is_additive_and_rejects_duplicate_rows_without_blocking_valid_rows()
    {
        var store = new MemoryStore();
        store.Rows.Add(new AffiliatePlatformRecord(7, "Keep Me", null, null, DateTime.UnixEpoch, DateTime.UnixEpoch) { NormalizedName = "KEEP ME" });
        store.Rows.Add(new AffiliatePlatformRecord(8, "Preserve Me", null, null, DateTime.UnixEpoch, DateTime.UnixEpoch) { NormalizedName = "PRESERVE ME" });
        var service = CreateService(store);

        var result = await service.Commit("writer", [
            new(" keep me ", null, "updated"),
            new("Fresh", "https://example.test/path", null),
            new(" fresh ", null, null)
        ], default);

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Equal(2, store.Rows.Count);
        Assert.Contains(store.Rows, x => x.Name == "keep me" && x.Notes == "updated");
        Assert.Contains(store.Rows, x => x.Name == "Preserve Me");
        Assert.Equal(new[] { 1, 2 }, result.Errors.Select(x => x.Index));
    }

    [Fact]
    public async Task Preview_requires_only_read_and_commit_checks_permissions_for_valid_row_actions()
    {
        var store = new MemoryStore();
        store.Rows.Add(new AffiliatePlatformRecord(7, "Existing", null, null, DateTime.UnixEpoch, DateTime.UnixEpoch) { NormalizedName = "EXISTING" });
        var service = CreateService(store);
        var preview = await service.Preview("reader", [new("Existing", null, null)], default);
        Assert.Equal(1, preview.UpdateCount);

        await Assert.ThrowsAsync<AdminForbiddenException>(() => service.Commit("reader", [new("New", null, null)], default));
        await service.Commit("updater", [new("Existing", null, "updated")], default);
        Assert.Equal("updated", Assert.Single(store.Rows).Notes);
    }

    [Fact]
    public async Task Delete_requires_permission_and_deduplicates_bulk_ids()
    {
        var store = new MemoryStore();
        store.Rows.Add(new AffiliatePlatformRecord(1, "One", null, null, DateTime.UnixEpoch, DateTime.UnixEpoch) { NormalizedName = "ONE" });
        var service = CreateService(store);

        var results = await service.BulkDelete("writer", [1, 1, 2], default);
        Assert.Equal(new[] { "deleted", "not_found" }, results.Select(x => x.Status));
        await Assert.ThrowsAsync<AdminForbiddenException>(() =>
            service.Delete("reader", 2, default));
    }

    [Fact]
    public async Task Invalid_absolute_url_and_missing_name_are_rejected()
    {
        var service = CreateService(new MemoryStore());
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("writer", new("Name", "/relative", null), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("writer", new("  ", null, null), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("writer", new(new string('n', 201), null, null), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Create("writer", new("Name", $"https://example.test/{new string('x', 2049)}", null), default));
    }

    private static AffiliatePlatformService CreateService(MemoryStore store) =>
        new(store, new PermissionReader(), TimeProvider.System);

    private sealed class PermissionReader : IAdminPermissionReader
    {
        public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) =>
            Task.FromResult(subject == "writer" || subject == "reader" && permission == AdminPermissions.AffiliatePlatformsRead ||
                subject == "updater" && permission is AdminPermissions.AffiliatePlatformsRead or AdminPermissions.AffiliatePlatformsUpdate);
    }

    private sealed class MemoryStore : IAffiliatePlatformStore
    {
        public List<AffiliatePlatformRecord> Rows { get; } = [];
        private int nextId = 1;

        public Task<IReadOnlyList<AffiliatePlatformRecord>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<AffiliatePlatformRecord>>(Rows.ToArray());
        public Task<AffiliatePlatformRecord?> Find(int id, CancellationToken ct) => Task.FromResult(Rows.SingleOrDefault(x => x.Id == id));
        public Task<AffiliatePlatformRecord?> FindByName(string normalizedName, int? excludingId, CancellationToken ct) =>
            Task.FromResult(Rows.SingleOrDefault(x => x.NormalizedName == normalizedName && x.Id != excludingId));

        public Task<AffiliatePlatformRecord> Save(int? id, AffiliatePlatformNormalizedInput input, DateTime nowUtc, CancellationToken ct)
        {
            var current = id.HasValue ? Rows.Single(x => x.Id == id.Value) : null;
            var row = new AffiliatePlatformRecord(current?.Id ?? nextId++, input.Name, input.HomeUrl, input.Notes,
                current?.CreatedAtUtc ?? nowUtc, nowUtc) { NormalizedName = input.NormalizedName };
            if (current is not null) Rows.Remove(current);
            Rows.Add(row);
            return Task.FromResult(row);
        }

        public Task<bool> Delete(int id, CancellationToken ct) => Task.FromResult(Rows.RemoveAll(x => x.Id == id) > 0);
        public Task<IReadOnlyList<AffiliatePlatformDeleteResult>> BulkDelete(IReadOnlyList<int> ids, CancellationToken ct)
        {
            var found = Rows.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToHashSet();
            Rows.RemoveAll(x => found.Contains(x.Id));
            return Task.FromResult<IReadOnlyList<AffiliatePlatformDeleteResult>>(ids.Select(id => new AffiliatePlatformDeleteResult(id, found.Contains(id) ? "deleted" : "not_found")).ToArray());
        }

        public Task<IReadOnlyDictionary<string, AffiliatePlatformRecord>> FindByNames(IReadOnlyCollection<string> normalizedNames, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, AffiliatePlatformRecord>>(Rows.Where(x => normalizedNames.Contains(x.NormalizedName)).ToDictionary(x => x.NormalizedName, StringComparer.Ordinal));

        public Task<AffiliatePlatformImportWriteResult> CommitImport(IReadOnlyList<AffiliatePlatformNormalizedInput> inputs, bool canCreate, bool canUpdate, DateTime nowUtc, CancellationToken ct)
        {
            var created = 0;
            var updated = 0;
            foreach (var input in inputs)
            {
                var current = Rows.SingleOrDefault(x => x.NormalizedName == input.NormalizedName);
                if (current is null) { Rows.Add(new AffiliatePlatformRecord(nextId++, input.Name, input.HomeUrl, input.Notes, nowUtc, nowUtc) { NormalizedName = input.NormalizedName }); created++; }
                else { Rows.Remove(current); Rows.Add(current with { Name = input.Name, HomeUrl = input.HomeUrl, Notes = input.Notes, UpdatedAtUtc = nowUtc }); updated++; }
            }
            return Task.FromResult(new AffiliatePlatformImportWriteResult(created, updated));
        }
    }
}
