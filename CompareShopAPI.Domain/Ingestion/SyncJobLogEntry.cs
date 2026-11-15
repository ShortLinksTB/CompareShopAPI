namespace CompareShopAPI.Domain.Ingestion;

public sealed class SyncJobLogEntry
{
    private SyncJobLogEntry() { }
    public SyncJobLogEntry(int jobId, DateTime occurredAtUtc, string level, string step, string message,
        string? exceptionType = null, int? httpStatusCode = null, int? durationMs = null)
    {
        if (jobId <= 0) throw new ArgumentOutOfRangeException(nameof(jobId));
        Id = 0; JobId = jobId; OccurredAtUtc = DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc);
        Level = Limit(level, 16); Step = Limit(step, 80); Message = Limit(message, 2000);
        ExceptionType = exceptionType is null ? null : Limit(exceptionType, 200);
        HttpStatusCode = httpStatusCode; DurationMs = durationMs;
    }
    public int Id { get; private set; }
    public int JobId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public string Level { get; private set; } = "Info";
    public string Step { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string? ExceptionType { get; private set; }
    public int? HttpStatusCode { get; private set; }
    public int? DurationMs { get; private set; }
    private static string Limit(string value, int size) => string.IsNullOrWhiteSpace(value) ? "unspecified" : value[..Math.Min(value.Length, size)];
}
