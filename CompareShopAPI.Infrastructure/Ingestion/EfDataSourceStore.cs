using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Infrastructure.Persistence;
using System.Data;
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
            source.NextAttemptAtUtc,
            db.PartnerShops.Where(shop => shop.Id == source.PartnerShopId).Select(shop => shop.Name).FirstOrDefault()))
        .ToListAsync(ct);

    public async Task<DataSourceSummary?> Configure(int id, DataSourceConfigurationInput input, string actor, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var source = await db.DataSources.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (source is null) { await tx.RollbackAsync(ct); return null; }
        if (input.PartnerShopId is int shopId && !await db.PartnerShops.AnyAsync(shop => shop.Id == shopId && shop.IsActive, ct))
            throw new ArgumentException("Partner shop must exist and be active.", nameof(input));

        var before = new { state = source.State, intervalHours = source.IntervalHours, partnerShopId = source.PartnerShopId };
        source.Configure(input.State, input.IntervalHours, input.PartnerShopId);
        var after = new { state = source.State, intervalHours = source.IntervalHours, partnerShopId = source.PartnerShopId };
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            ActorSubject = actor,
            Action = "update",
            ResourceType = "data_source",
            ResourceId = source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CreatedAtUtc = DateTime.UtcNow,
            BeforeJson = System.Text.Json.JsonSerializer.Serialize(before),
            AfterJson = System.Text.Json.JsonSerializer.Serialize(after)
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var partnerShopDisplayName = input.PartnerShopId is int partnerShopId
            ? await db.PartnerShops.Where(shop => shop.Id == partnerShopId).Select(shop => shop.Name).SingleOrDefaultAsync(ct)
            : null;
        return new(source.Id, source.Key, source.DisplayName, source.CategoryPath, source.PartnerShopId, source.State,
            source.IntervalHours, source.CreatedAtUtc, source.LastCompletedAtUtc, source.NextAttemptAtUtc, partnerShopDisplayName);
    }

    public async Task<ManualSyncResult> QueueManualSync(int id, DateTime queuedAtUtc, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // The source row is the per-source mutex shared with worker claims; this keeps the
        // eligibility check and queued insert single-winner under concurrent manual requests.
        var source = await db.DataSources.FromSqlInterpolated($"SELECT * FROM [DataSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .SingleOrDefaultAsync(ct);
        if (source is null) { await tx.RollbackAsync(ct); return new(ManualSyncResultKind.Missing); }
        if (source.State != DataSourceState.Active || await db.SyncJobs.AnyAsync(job => job.DataSourceId == id &&
            (job.State == SyncJobState.Queued || job.State == SyncJobState.Running), ct))
        {
            await tx.RollbackAsync(ct);
            return new(ManualSyncResultKind.Unavailable);
        }

        var job = new SyncJob(id, queuedAtUtc);
        db.SyncJobs.Add(job);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new(ManualSyncResultKind.Queued, new SyncJobSummary(job.Id, source.Id, source.DisplayName, job.State,
            job.QueuedAtUtc, job.StartedAtUtc, job.FinishedAtUtc, job.SeenCount, job.ImportedCount, job.PendingCount,
            job.ErrorCode, job.ErrorSummary, job.RetryOfJobId));
    }
}
