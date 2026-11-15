using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Infrastructure.Persistence;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Ingestion;

public sealed class EfDataSourceStore(CompareShopDbContext db) : IDataSourceStore
{
    private sealed record MutableConfiguration(DataSourceState State, int IntervalHours, int? PartnerShopId);

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

    public Task<bool> PartnerShopIsUsable(int partnerShopId, CancellationToken ct) => db.PartnerShops.AsNoTracking().AnyAsync(x => x.Id == partnerShopId && x.IsActive, ct);
    public Task<bool> Exists(int id, CancellationToken ct) => db.DataSources.AsNoTracking().AnyAsync(x => x.Id == id, ct);

    public async Task<DataSourceSummary?> Configure(int id, DataSourceConfiguration configuration, string actorSubject, DateTime nowUtc, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var source = await db.DataSources.FromSqlInterpolated($"SELECT * FROM [DataSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(ct);
        if (source is null) { await tx.RollbackAsync(ct); return null; }
        if (configuration.PartnerShopId is int shopId && !await db.PartnerShops.AnyAsync(x => x.Id == shopId && x.IsActive, ct))
        { await tx.RollbackAsync(ct); throw new ArgumentException("PartnerShopId must reference an active partner shop."); }
        var before = new MutableConfiguration(source.State, source.IntervalHours, source.PartnerShopId);
        try { source.Configure(configuration.State, configuration.IntervalHours, configuration.PartnerShopId); }
        catch (InvalidOperationException) { await tx.RollbackAsync(ct); throw; }
        var after = new MutableConfiguration(source.State, source.IntervalHours, source.PartnerShopId);
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            ActorSubject = actorSubject,
            Action = "configure",
            ResourceType = "data_source",
            ResourceId = id.ToString(),
            CreatedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            BeforeJson = JsonSerializer.Serialize(before),
            AfterJson = JsonSerializer.Serialize(after)
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new DataSourceSummary(source.Id, source.Key, source.DisplayName, source.CategoryPath, source.PartnerShopId, source.State, source.IntervalHours, source.CreatedAtUtc, source.LastCompletedAtUtc, source.NextAttemptAtUtc);
    }
}
