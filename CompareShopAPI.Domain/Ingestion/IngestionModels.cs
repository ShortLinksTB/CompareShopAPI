namespace CompareShopAPI.Domain.Ingestion;

public enum DataSourceState { Active, Paused, Error }
public enum SyncJobState { Queued, Running, Success, Partial, Failed }

public sealed class DataSource
{
    private DataSource() { }
    public DataSource(string key, string displayName, string categoryPath, int? partnerShopId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 80) throw new ArgumentException("Invalid source key.", nameof(key));
        if (string.IsNullOrWhiteSpace(categoryPath) || categoryPath != "/bia") throw new ArgumentException("Only the validated /bia category is supported.", nameof(categoryPath));
        Key = key; DisplayName = displayName; CategoryPath = categoryPath; PartnerShopId = partnerShopId;
        State = DataSourceState.Active; IntervalHours = 72; CreatedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
    }
    public int Id { get; private set; }
    public string Key { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string CategoryPath { get; private set; } = "/bia";
    public int? PartnerShopId { get; private set; }
    public DataSourceState State { get; private set; }
    public int IntervalHours { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastCompletedAtUtc { get; private set; }
    public DateTime? NextAttemptAtUtc { get; private set; }
    public bool IsDue(DateTime nowUtc) => State == DataSourceState.Active && (LastCompletedAtUtc is null || nowUtc >= LastCompletedAtUtc.Value.AddHours(IntervalHours)) && (NextAttemptAtUtc is null || nowUtc >= NextAttemptAtUtc.Value);
    public void Complete(DateTime atUtc) { LastCompletedAtUtc = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc); NextAttemptAtUtc = null; }
    public void ScheduleFailureRetry(DateTime atUtc) => NextAttemptAtUtc = DateTime.SpecifyKind(atUtc, DateTimeKind.Utc).AddHours(6);
    public void Pause() { State = DataSourceState.Paused; NextAttemptAtUtc = null; }
    public void AttachPartnerShop(int partnerShopId) { if (partnerShopId <= 0) throw new ArgumentOutOfRangeException(nameof(partnerShopId)); PartnerShopId = partnerShopId; }
    public void MarkError() => State = DataSourceState.Error;
}

public sealed class SyncJob
{
    public const int LeaseDurationMinutes = 30;
    private SyncJob() { }
    public SyncJob(int dataSourceId, DateTime queuedAtUtc, int? retryOfJobId = null)
    {
        if (dataSourceId <= 0) throw new ArgumentOutOfRangeException(nameof(dataSourceId));
        DataSourceId = dataSourceId; QueuedAtUtc = DateTime.SpecifyKind(queuedAtUtc, DateTimeKind.Utc); State = SyncJobState.Queued; RetryOfJobId = retryOfJobId;
    }
    public int Id { get; private set; }
    public int DataSourceId { get; private set; }
    public SyncJobState State { get; private set; }
    public DateTime QueuedAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public int SeenCount { get; private set; }
    public int ImportedCount { get; private set; }
    public int PendingCount { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorSummary { get; private set; }
    public int? RetryOfJobId { get; private set; }
    public string LeaseId { get; private set; } = "";
    public DateTime? LeaseExpiresAtUtc { get; private set; }
    public bool Start(DateTime nowUtc, string leaseId, DateTime leaseExpiresAtUtc)
    {
        if (State != SyncJobState.Queued) return false;
        if (string.IsNullOrWhiteSpace(leaseId) || leaseId.Length > 64) throw new ArgumentException("Invalid lease ID.", nameof(leaseId));
        if (leaseExpiresAtUtc <= nowUtc) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));
        State = SyncJobState.Running;
        StartedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        LeaseId = leaseId;
        LeaseExpiresAtUtc = DateTime.SpecifyKind(leaseExpiresAtUtc, DateTimeKind.Utc);
        return true;
    }
    public void Finish(SyncJobState state, DateTime nowUtc, int seen, int imported, int pending, string? errorCode = null, string? errorSummary = null)
    {
        if (State != SyncJobState.Running || state is SyncJobState.Queued or SyncJobState.Running) throw new InvalidOperationException("Only running jobs can finish in a terminal state.");
        State = state; FinishedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc); SeenCount = Math.Max(0, seen); ImportedCount = Math.Max(0, imported); PendingCount = Math.Max(0, pending);
        LeaseExpiresAtUtc = null;
        ErrorCode = errorCode is null ? null : errorCode[..Math.Min(errorCode.Length, 50)]; ErrorSummary = errorSummary is null ? null : new string(errorSummary.Where(c => !char.IsControl(c)).Take(240).ToArray());
    }
}

public sealed class SourceItemMapping
{
    private SourceItemMapping() { }
    public SourceItemMapping(int dataSourceId, string externalSku, string name, string? brand)
    {
        if (dataSourceId <= 0 || string.IsNullOrWhiteSpace(externalSku) || externalSku.Length > 160) throw new ArgumentException("Invalid source item identity.");
        DataSourceId = dataSourceId; ExternalSku = externalSku; Name = name[..Math.Min(name.Length, 300)]; Brand = brand is null ? null : brand[..Math.Min(brand.Length, 200)];
    }
    public int Id { get; private set; }
    public int DataSourceId { get; private set; }
    public string ExternalSku { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string? Brand { get; private set; }
    public int? ProductId { get; private set; }
    public int? ProductOfferId { get; private set; }
    public bool IsApproved => ProductId.HasValue;
    public void Approve(int productId) { if (ProductId.HasValue || productId <= 0) throw new InvalidOperationException("Mapping cannot be approved."); ProductId = productId; }
    public void UpdateSnapshot(string name, string? brand) { Name = name[..Math.Min(name.Length, 300)]; Brand = brand is null ? null : brand[..Math.Min(brand.Length, 200)]; }
    public void SetOffer(int offerId) { if (offerId <= 0) throw new ArgumentOutOfRangeException(nameof(offerId)); ProductOfferId = offerId; }
}

public sealed record ScrapedOffer(string ExternalSku, string Name, string? Brand, int PriceVnd, string Url);
