using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Ingestion;

public sealed class EfDataSourceStore(CompareShopDbContext db) : IDataSourceStore
{
    public async Task<IReadOnlyList<DataSourceSummary>> ListDataSources(CancellationToken ct) => await db.DataSources
        .AsNoTracking()
        .OrderBy(source => source.Id)
        .Select(source => new DataSourceSummary(
            source.Id,
            source.Key,
            source.DisplayName,
            source.CategoryPath,
            source.PartnerShopId,
            source.State,
            source.IntervalHours,
            source.CreatedAtUtc,
            source.LastCompletedAtUtc,
            source.NextAttemptAtUtc))
        .ToListAsync(ct);
}
