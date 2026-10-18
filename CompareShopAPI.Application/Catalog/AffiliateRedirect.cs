using CompareShopAPI.Domain.Catalog;

namespace CompareShopAPI.Application.Catalog;

public interface IAffiliateRedirectRepository
{
    Task<AffiliateLink?> FindRedirectableLink(int offerId, CancellationToken ct);
    Task<bool> RecordClick(int affiliateLinkId, DateTime clickedAtUtc, string? clientIpDigest, CancellationToken ct);
}

public interface IClientIpHasher
{
    string? Hash(string? clientIp);
}

public enum AffiliateRedirectRateLimitStatus { Allowed, Rejected, FailOpen }

public sealed record AffiliateRedirectRateLimitDecision(AffiliateRedirectRateLimitStatus Status, int RetryAfterSeconds = 0)
{
    public bool IsRejected => Status == AffiliateRedirectRateLimitStatus.Rejected;
    public static AffiliateRedirectRateLimitDecision Allow() => new(AffiliateRedirectRateLimitStatus.Allowed);
    public static AffiliateRedirectRateLimitDecision Reject(int seconds) => new(AffiliateRedirectRateLimitStatus.Rejected, Math.Max(1, seconds));
    public static AffiliateRedirectRateLimitDecision Open() => new(AffiliateRedirectRateLimitStatus.FailOpen);
}

public sealed class AffiliateRedirectRateLimitOptions
{
    public const string SectionName = "RateLimiting:AffiliateRedirect";
    public int PermitLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 60;
    public int GraceSeconds { get; set; } = 5;
    public string[] TrustedProxies { get; set; } = [];
}

public interface IAffiliateRedirectRateLimiter
{
    Task<AffiliateRedirectRateLimitDecision> TryAcquireAsync(string canonicalClientIp, CancellationToken ct);
}

public sealed record AffiliateRedirectResult(bool Found, bool TrackingFailed, Uri? Destination)
{
    public static AffiliateRedirectResult NotFound { get; } = new(false, false, null);
    public static AffiliateRedirectResult PersistenceFailure { get; } = new(false, true, null);
}

public sealed class AffiliateRedirectService(IAffiliateRedirectRepository repository, IClientIpHasher ipHasher, TimeProvider clock)
{
    public async Task<AffiliateRedirectResult> Redirect(int offerId, string? clientIp, CancellationToken ct)
    {
        if (offerId <= 0) return AffiliateRedirectResult.NotFound;
        var link = await repository.FindRedirectableLink(offerId, ct);
        if (link is null || !link.TryGetRedirectDestination(out var destination)) return AffiliateRedirectResult.NotFound;

        var digest = ipHasher.Hash(clientIp);
        if (!await repository.RecordClick(link.Id, clock.GetUtcNow().UtcDateTime, digest, ct)) return AffiliateRedirectResult.PersistenceFailure;
        return new AffiliateRedirectResult(true, false, destination);
    }
}
