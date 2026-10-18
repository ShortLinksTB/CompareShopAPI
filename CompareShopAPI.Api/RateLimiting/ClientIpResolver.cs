using System.Net;
using CompareShopAPI.Application.Catalog;
using Microsoft.Extensions.Options;

namespace CompareShopAPI.Api.RateLimiting;

public interface IClientIpResolver
{
    string? Resolve(HttpContext context);
}

public sealed class ClientIpResolver(IOptions<AffiliateRedirectRateLimitOptions> options) : IClientIpResolver
{
    public string? Resolve(HttpContext context)
    {
        var remote = Normalize(context.Connection.RemoteIpAddress);
        if (remote is null) return null;
        var trustedProxies = options.Value.TrustedProxies.Select(Normalize).Where(ip => ip is not null).ToHashSet(StringComparer.Ordinal);
        if (!trustedProxies.Contains(remote)) return remote;
        var current = remote;
        var forwarded = (context.Request.Headers.TryGetValue("X-Forwarded-For", out var values) ? values.ToString() : string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Reverse();
        foreach (var forwardedIp in forwarded)
        {
            if (!trustedProxies.Contains(current)) break;
            var normalized = Normalize(forwardedIp);
            if (normalized is null) break;
            current = normalized;
        }
        return current;
    }

    private static string? Normalize(IPAddress? address) => address is null ? null : Normalize(address.ToString());
    private static string? Normalize(string value) => IPAddress.TryParse(value, out var address)
        ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
        : null;
}
