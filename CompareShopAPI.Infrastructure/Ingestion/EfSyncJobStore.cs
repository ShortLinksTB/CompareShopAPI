using System.Data;
using CompareShopAPI.Application.Ingestion;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Ingestion;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Ingestion;

public sealed class EfSyncJobStore(CompareShopDbContext db) : ISyncJobStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(SyncJob.LeaseDurationMinutes);

    public async Task<IReadOnlyList<DataSource>> DueSources(DateTime nowUtc, CancellationToken ct) => await db.DataSources
        .Where(s => s.State == DataSourceState.Active && (db.SyncJobs.Any(j => j.DataSourceId == s.Id && j.State == SyncJobState.Queued)
                || db.SyncJobs.Any(j => j.DataSourceId == s.Id && j.State == SyncJobState.Running && (j.LeaseExpiresAtUtc == null || j.LeaseExpiresAtUtc <= nowUtc))
                || ((s.LastCompletedAtUtc == null || s.LastCompletedAtUtc.Value.AddHours(s.IntervalHours) <= nowUtc) && (s.NextAttemptAtUtc == null || s.NextAttemptAtUtc <= nowUtc)))
            && !db.SyncJobs.Any(j => j.DataSourceId == s.Id && j.State == SyncJobState.Running && j.LeaseExpiresAtUtc > nowUtc))
        .ToListAsync(ct);

    public async Task<SyncJob?> TryClaim(int sourceId, DateTime nowUtc, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var source = await db.DataSources.SingleOrDefaultAsync(s => s.Id == sourceId && s.State == DataSourceState.Active, ct);
        if (source is null) { await tx.RollbackAsync(ct); return null; }
        var expiredJobs = await db.SyncJobs.Where(j => j.DataSourceId == sourceId && j.State == SyncJobState.Running
            && (j.LeaseExpiresAtUtc == null || j.LeaseExpiresAtUtc <= nowUtc)).ToListAsync(ct);
        foreach (var expiredJob in expiredJobs)
            expiredJob.Finish(SyncJobState.Failed, nowUtc, expiredJob.SeenCount, expiredJob.ImportedCount, expiredJob.PendingCount,
                "lease_expired", "Worker lease expired before the job completed.");
        if (expiredJobs.Count > 0) await db.SaveChangesAsync(ct);
        if (await db.SyncJobs.AnyAsync(j => j.DataSourceId == sourceId && j.State == SyncJobState.Running && j.LeaseExpiresAtUtc > nowUtc, ct))
        { await tx.RollbackAsync(ct); return null; }
        var job = await db.SyncJobs.Where(x => x.DataSourceId == sourceId && x.State == SyncJobState.Queued).OrderBy(x => x.QueuedAtUtc).FirstOrDefaultAsync(ct);
        if (job is null)
        {
            if ((source.LastCompletedAtUtc is not null && nowUtc < source.LastCompletedAtUtc.Value.AddHours(source.IntervalHours))
                || (source.NextAttemptAtUtc is not null && nowUtc < source.NextAttemptAtUtc.Value))
            { await tx.CommitAsync(ct); return null; }
            job = new SyncJob(sourceId, nowUtc); db.SyncJobs.Add(job);
        }
        job.Start(nowUtc, Guid.NewGuid().ToString("N"), nowUtc.Add(LeaseDuration));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return job;
    }

    public async Task<bool> ShopIsUsable(int sourceId, CancellationToken ct)
    {
        var source = await db.DataSources.SingleOrDefaultAsync(x => x.Id == sourceId, ct);
        if (source is null) return false;
        if (source.PartnerShopId is null)
        {
            var repaired = await db.PartnerShops.Where(x => x.Slug == "bach-hoa-xanh" || x.Slug == "bachhoaxanh" || x.Slug == "bhx").OrderByDescending(x => x.IsActive).FirstOrDefaultAsync(ct);
            if (repaired is null) return false;
            source.AttachPartnerShop(repaired.Id);
            await db.SaveChangesAsync(ct);
        }
        return await db.PartnerShops.AnyAsync(x => x.Id == source.PartnerShopId && x.IsActive, ct);
    }

    public async Task<ImportCounts> Import(int sourceId, IReadOnlyList<ScrapedOffer> rows, bool fullSnapshot, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var source = await db.DataSources.SingleAsync(x => x.Id == sourceId, ct);
        if (source.PartnerShopId is not int shopId || !await db.PartnerShops.AnyAsync(x => x.Id == shopId && x.IsActive, ct)) throw new InvalidOperationException("Partner shop is unavailable.");
        var imported = 0; var pending = 0; var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ExternalSku) || row.PriceVnd <= 0 || !Uri.TryCreate(row.Url, UriKind.Absolute, out var url) || url.Scheme != "https")
                throw new InvalidDataException("Imported offer did not pass validation.");
            if (!seen.Add(row.ExternalSku)) continue;
            var mapping = await db.SourceItemMappings.SingleOrDefaultAsync(m => m.DataSourceId == sourceId && m.ExternalSku == row.ExternalSku, ct);
            if (mapping is null)
            {
                mapping = new SourceItemMapping(sourceId, row.ExternalSku, row.Name, row.Brand);
                db.SourceItemMappings.Add(mapping);
                db.ProductMatchings.Add(ProductMatching.ForSourceItem(row.Name, row.Brand, DateTime.UtcNow, sourceId, row.ExternalSku));
                pending++; continue;
            }
            mapping.UpdateSnapshot(row.Name, row.Brand);
            if (mapping.ProductId is not int productId) { pending++; continue; }
            if (mapping.ProductOfferId is int offerId)
            {
                var offer = await db.ProductOffers.SingleOrDefaultAsync(o => o.Id == offerId && o.ProductId == productId && o.PartnerShopId == shopId, ct);
                if (offer is null) throw new InvalidOperationException("Mapped offer is inconsistent.");
                offer.PriceVnd = row.PriceVnd; offer.Url = row.Url; offer.UpdatedAtUtc = DateTime.UtcNow; offer.IsActive = true;
            }
            else
            {
                var offer = new ProductOffer { ProductId = productId, PartnerShopId = shopId, PriceVnd = row.PriceVnd, Url = row.Url, UpdatedAtUtc = DateTime.UtcNow, IsActive = true };
                db.ProductOffers.Add(offer);
                await db.SaveChangesAsync(ct); mapping.SetOffer(offer.Id);
            }
            imported++;
        }
        if (fullSnapshot && seen.Count > 0)
        {
            var mappedOffers = db.SourceItemMappings.Where(m => m.DataSourceId == sourceId && m.ProductOfferId != null && !seen.Contains(m.ExternalSku)).Select(m => m.ProductOfferId!.Value);
            await db.ProductOffers.Where(o => mappedOffers.Contains(o.Id)).ExecuteUpdateAsync(update => update.SetProperty(o => o.IsActive, false), ct);
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(imported, pending);
    }

    public async Task Complete(int sourceId, SyncJob job, SyncJobState state, DateTime finishedUtc, int seen, int imported, int pending, string? errorCode, string? errorSummary, CancellationToken ct)
    {
        // A failed import transaction may have left rolled-back entities tracked. Reload terminal state from SQL.
        db.ChangeTracker.Clear();
        var persisted = await db.SyncJobs.SingleAsync(x => x.Id == job.Id && x.State == SyncJobState.Running, ct);
        persisted.Finish(state, finishedUtc, seen, imported, pending, errorCode, errorSummary);
        if (state is SyncJobState.Success or SyncJobState.Partial)
        {
            var source = await db.DataSources.SingleAsync(x => x.Id == sourceId, ct); source.Complete(finishedUtc);
        }
        else
        {
            var source = await db.DataSources.SingleAsync(x => x.Id == sourceId, ct); source.ScheduleFailureRetry(finishedUtc);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SyncJobSummary>> ListJobs(CancellationToken ct) => await db.SyncJobs.AsNoTracking().OrderByDescending(x => x.QueuedAtUtc).Take(100)
        .Join(db.DataSources.AsNoTracking(), job => job.DataSourceId, source => source.Id,
            (job, source) => new SyncJobSummary(job.Id, job.DataSourceId, source.DisplayName, job.State, job.QueuedAtUtc, job.StartedAtUtc, job.FinishedAtUtc, job.SeenCount, job.ImportedCount, job.PendingCount, job.ErrorCode, job.ErrorSummary, job.RetryOfJobId)).ToListAsync(ct);

    public async Task<SyncJobSummary?> QueueRetry(int jobId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var previous = await db.SyncJobs.SingleOrDefaultAsync(x => x.Id == jobId && (x.State == SyncJobState.Failed || x.State == SyncJobState.Partial), ct);
        if (previous is null) { await tx.RollbackAsync(ct); return null; }
        var existingRetry = await db.SyncJobs.SingleOrDefaultAsync(x => x.RetryOfJobId == previous.Id, ct);
        if (existingRetry is not null)
        {
            var existingName = await db.DataSources.Where(x => x.Id == existingRetry.DataSourceId).Select(x => x.DisplayName).SingleAsync(ct);
            await tx.CommitAsync(ct);
            return new(existingRetry.Id, existingRetry.DataSourceId, existingName, existingRetry.State, existingRetry.QueuedAtUtc, existingRetry.StartedAtUtc, existingRetry.FinishedAtUtc, existingRetry.SeenCount, existingRetry.ImportedCount, existingRetry.PendingCount, existingRetry.ErrorCode, existingRetry.ErrorSummary, existingRetry.RetryOfJobId);
        }
        var retry = new SyncJob(previous.DataSourceId, DateTime.UtcNow, previous.Id);
        db.SyncJobs.Add(retry); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        var sourceName = await db.DataSources.Where(x => x.Id == retry.DataSourceId).Select(x => x.DisplayName).SingleAsync(ct);
        return new(retry.Id, retry.DataSourceId, sourceName, retry.State, retry.QueuedAtUtc, null, null, 0, 0, 0, null, null, retry.RetryOfJobId);
    }
}
