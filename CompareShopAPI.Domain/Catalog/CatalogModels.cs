namespace CompareShopAPI.Domain.Catalog;

public sealed class Category { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public bool IsActive { get; set; } = true; }
public sealed class PartnerShop { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public string? WebsiteUrl { get; set; } public bool IsActive { get; set; } = true; }
public sealed class Product { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public string? Brand { get; set; } public string? Description { get; set; } public int CategoryId { get; set; } public Category Category { get; set; } = null!; public bool IsActive { get; set; } = true; public List<ProductOffer> Offers { get; set; } = []; public List<SpecificationGroup> SpecificationGroups { get; set; } = []; }
public sealed class ProductOffer { public int Id { get; set; } public int ProductId { get; set; } public Product Product { get; set; } = null!; public int PartnerShopId { get; set; } public PartnerShop PartnerShop { get; set; } = null!; public int PriceVnd { get; set; } public string? Url { get; set; } public DateTime UpdatedAtUtc { get; set; } public bool IsActive { get; set; } = true; public bool IsOutlier { get; set; } }
public sealed class SpecificationGroup { public int Id { get; set; } public int ProductId { get; set; } public Product Product { get; set; } = null!; public required string Name { get; set; } public int SortOrder { get; set; } public List<SpecificationItem> Items { get; set; } = []; }
public sealed class SpecificationItem { public int Id { get; set; } public int SpecificationGroupId { get; set; } public SpecificationGroup Group { get; set; } = null!; public required string Name { get; set; } public required string Value { get; set; } public int SortOrder { get; set; } }

public sealed class AffiliateLink
{
    public int Id { get; set; }
    public int ProductOfferId { get; set; }
    public ProductOffer ProductOffer { get; set; } = null!;
    public required string DestinationUrl { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsStale { get; set; }
    public int ClickCount { get; set; }
    public int ConversionCount { get; set; }
    public decimal Revenue { get; set; }
    public DateTime? LastCheckedAtUtc { get; set; }

    public bool TryGetRedirectDestination(out Uri? destination)
    {
        destination = null;
        if (!IsEnabled || IsStale || string.IsNullOrWhiteSpace(DestinationUrl) || DestinationUrl.Any(char.IsControl)) return false;
        if (!Uri.TryCreate(DestinationUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(parsed.Host) || !parsed.IsWellFormedOriginalString()) return false;
        destination = parsed;
        return true;
    }
}

public sealed class AffiliateClick
{
    public long Id { get; set; }
    public int AffiliateLinkId { get; set; }
    public AffiliateLink AffiliateLink { get; set; } = null!;
    public DateTime ClickedAtUtc { get; set; }
    public string? ClientIpDigest { get; set; }
}

public static class CatalogOfferPolicy
{
    public const int FreshnessDays = 30;
}
