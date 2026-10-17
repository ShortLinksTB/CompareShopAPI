using System.Security.Cryptography;
using System.Text;
using CompareShopAPI.Application.Catalog;
using Microsoft.Extensions.Configuration;

namespace CompareShopAPI.Infrastructure.Catalog;

public sealed class HmacClientIpHasher(IConfiguration configuration) : IClientIpHasher
{
    public string? Hash(string? clientIp)
    {
        var key = configuration["AffiliateTracking:IpHashKey"];
        if (string.IsNullOrWhiteSpace(key) || !System.Net.IPAddress.TryParse(clientIp, out var address)) return null;
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(address.ToString()));
        return Convert.ToHexStringLower(digest);
    }
}
