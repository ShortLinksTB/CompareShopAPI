using System.Data;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Admin;

public sealed class EfAffiliatePlatformStore(CompareShopDbContext db) : IAffiliatePlatformStore
{
    public async Task<IReadOnlyList<AffiliatePlatformRecord>> List(CancellationToken ct) => await db.AffiliatePlatforms.AsNoTracking()
        .OrderBy(x => x.NormalizedName).Select(ToRecord()).ToListAsync(ct);

    public Task<AffiliatePlatformRecord?> Find(int id, CancellationToken ct) => db.AffiliatePlatforms.AsNoTracking()
        .Where(x => x.Id == id).Select(ToRecord()).SingleOrDefaultAsync(ct);

    public Task<AffiliatePlatformRecord?> FindByName(string normalizedName, int? excludingId, CancellationToken ct) => db.AffiliatePlatforms.AsNoTracking()
        .Where(x => x.NormalizedName == normalizedName && (!excludingId.HasValue || x.Id != excludingId.Value))
        .Select(ToRecord()).SingleOrDefaultAsync(ct);

    public async Task<AffiliatePlatformRecord> Save(int? id, AffiliatePlatformNormalizedInput input, DateTime nowUtc, CancellationToken ct)
    {
        var item = id.HasValue ? await db.AffiliatePlatforms.SingleOrDefaultAsync(x => x.Id == id.Value, ct) : null;
        if (id.HasValue && item is null) throw new KeyNotFoundException();
        item ??= new AffiliatePlatform(input.Name, input.NormalizedName, input.HomeUrl, input.Notes, nowUtc);
        if (id.HasValue) item.Update(input.Name, input.NormalizedName, input.HomeUrl, input.Notes, nowUtc);
        else db.AffiliatePlatforms.Add(item);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex)) { throw new AffiliatePlatformDuplicateException(); }
        return ToRecord(item);
    }

    public async Task<bool> Delete(int id, CancellationToken ct) => await db.AffiliatePlatforms.Where(x => x.Id == id).ExecuteDeleteAsync(ct) > 0;

    public async Task<IReadOnlyList<AffiliatePlatformDeleteResult>> BulkDelete(IReadOnlyList<int> ids, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var found = await db.AffiliatePlatforms.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        if (found.Count > 0) await db.AffiliatePlatforms.Where(x => found.Contains(x.Id)).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        var deleted = found.ToHashSet();
        return ids.Select(id => new AffiliatePlatformDeleteResult(id, deleted.Contains(id) ? "deleted" : "not_found")).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, AffiliatePlatformRecord>> FindByNames(IReadOnlyCollection<string> normalizedNames, CancellationToken ct)
    {
        if (normalizedNames.Count == 0) return new Dictionary<string, AffiliatePlatformRecord>(StringComparer.Ordinal);
        return await db.AffiliatePlatforms.AsNoTracking().Where(x => normalizedNames.Contains(x.NormalizedName))
            .Select(x => new { x.NormalizedName, Record = new AffiliatePlatformRecord(x.Id, x.Name, x.HomeUrl, x.Notes, x.CreatedAtUtc, x.UpdatedAtUtc) { NormalizedName = x.NormalizedName } })
            .ToDictionaryAsync(x => x.NormalizedName, x => x.Record, StringComparer.Ordinal, ct);
    }

    public async Task<AffiliatePlatformImportWriteResult> CommitImport(IReadOnlyList<AffiliatePlatformNormalizedInput> inputs, DateTime nowUtc, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var normalizedNames = inputs.Select(x => x.NormalizedName).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.AffiliatePlatforms.Where(x => normalizedNames.Contains(x.NormalizedName)).ToDictionaryAsync(x => x.NormalizedName, StringComparer.Ordinal, ct);
        var created = 0;
        var updated = 0;
        foreach (var input in inputs)
        {
            if (existing.TryGetValue(input.NormalizedName, out var item))
            {
                item.Update(input.Name, input.NormalizedName, input.HomeUrl, input.Notes, nowUtc);
                updated++;
            }
            else
            {
                item = new AffiliatePlatform(input.Name, input.NormalizedName, input.HomeUrl, input.Notes, nowUtc);
                db.AffiliatePlatforms.Add(item);
                existing.Add(input.NormalizedName, item);
                created++;
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex)) { throw new AffiliatePlatformDuplicateException(); }
        return new(created, updated);
    }

    private static System.Linq.Expressions.Expression<Func<AffiliatePlatform, AffiliatePlatformRecord>> ToRecord() =>
        x => new AffiliatePlatformRecord(x.Id, x.Name, x.HomeUrl, x.Notes, x.CreatedAtUtc, x.UpdatedAtUtc) { NormalizedName = x.NormalizedName };

    private static AffiliatePlatformRecord ToRecord(AffiliatePlatform x) =>
        new(x.Id, x.Name, x.HomeUrl, x.Notes, x.CreatedAtUtc, x.UpdatedAtUtc) { NormalizedName = x.NormalizedName };

    private static bool IsUniqueNameViolation(DbUpdateException exception) => exception.InnerException is SqlException { Number: 2601 or 2627 };
}
