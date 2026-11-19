namespace CompareShopAPI.Domain.Catalog;

public sealed class Country
{
    private static readonly HashSet<string> IsoAlpha2Codes = new(StringComparer.Ordinal)
    {
        "AD", "AE", "AF", "AG", "AI", "AL", "AM", "AO", "AQ", "AR", "AS", "AT", "AU", "AW", "AX", "AZ",
        "BA", "BB", "BD", "BE", "BF", "BG", "BH", "BI", "BJ", "BL", "BM", "BN", "BO", "BQ", "BR", "BS", "BT", "BV", "BW", "BY", "BZ",
        "CA", "CC", "CD", "CF", "CG", "CH", "CI", "CK", "CL", "CM", "CN", "CO", "CR", "CU", "CV", "CW", "CX", "CY", "CZ",
        "DE", "DJ", "DK", "DM", "DO", "DZ", "EC", "EE", "EG", "EH", "ER", "ES", "ET",
        "FI", "FJ", "FK", "FM", "FO", "FR",
        "GA", "GB", "GD", "GE", "GF", "GG", "GH", "GI", "GL", "GM", "GN", "GP", "GQ", "GR", "GS", "GT", "GU", "GW", "GY",
        "HK", "HM", "HN", "HR", "HT", "HU",
        "ID", "IE", "IL", "IM", "IN", "IO", "IQ", "IR", "IS", "IT",
        "JE", "JM", "JO", "JP",
        "KE", "KG", "KH", "KI", "KM", "KN", "KP", "KR", "KW", "KY", "KZ",
        "LA", "LB", "LC", "LI", "LK", "LR", "LS", "LT", "LU", "LV", "LY",
        "MA", "MC", "MD", "ME", "MF", "MG", "MH", "MK", "ML", "MM", "MN", "MO", "MP", "MQ", "MR", "MS", "MT", "MU", "MV", "MW", "MX", "MY", "MZ",
        "NA", "NC", "NE", "NF", "NG", "NI", "NL", "NO", "NP", "NR", "NU", "NZ",
        "OM",
        "PA", "PE", "PF", "PG", "PH", "PK", "PL", "PM", "PN", "PR", "PS", "PT", "PW", "PY",
        "QA",
        "RE", "RO", "RS", "RU", "RW",
        "SA", "SB", "SC", "SD", "SE", "SG", "SH", "SI", "SJ", "SK", "SL", "SM", "SN", "SO", "SR", "SS", "ST", "SV", "SX", "SY", "SZ",
        "TC", "TD", "TF", "TG", "TH", "TJ", "TK", "TL", "TM", "TN", "TO", "TR", "TT", "TV", "TW", "TZ",
        "UA", "UG", "UM", "US", "UY", "UZ",
        "VA", "VC", "VE", "VG", "VI", "VN", "VU",
        "WF", "WS",
        "YE", "YT",
        "ZA", "ZM", "ZW"
    };

    private Country() { }
    public Country(string code, string name, bool isActive = true) => Update(code, name, isActive);
    public int Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public void Update(string code, string name, bool isActive)
    {
        var normalizedCode = code?.Trim().ToUpperInvariant();
        var normalizedName = name?.Trim();
        if (normalizedCode is null || !IsoAlpha2Codes.Contains(normalizedCode))
            throw new ArgumentException("Country code must be a valid ISO 3166-1 alpha-2 code.", nameof(code));
        if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName.Length > 100)
            throw new ArgumentException("Country name is required and must be at most 100 characters.", nameof(name));
        Code = normalizedCode;
        Name = normalizedName;
        IsActive = isActive;
    }
}

public sealed class Category
{
    public int Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public int? ParentCategoryId { get; set; }
    public Category? ParentCategory { get; set; }
    public int CountryId { get; set; }
    public Country Country { get; set; } = null!;
    public byte Level { get; set; } = 1;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Category> Children { get; set; } = [];
}
public sealed class PartnerShop { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public string? WebsiteUrl { get; set; } public int CountryId { get; set; } public Country Country { get; set; } = null!; public bool IsActive { get; set; } = true; public bool ShowOnHome { get; set; } public int? HomeDisplayOrder { get; set; } }
public sealed class Product { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public string? Brand { get; set; } public string? Description { get; set; } public string? ImageUrl { get; set; } public string? SeoTitle { get; set; } public string? SeoDescription { get; set; } public int CategoryId { get; set; } public Category Category { get; set; } = null!; public bool IsActive { get; set; } = true; public List<ProductOffer> Offers { get; set; } = []; public List<SpecificationGroup> SpecificationGroups { get; set; } = []; public List<ProductImage> Images { get; set; } = []; }

public sealed class ProductImage
{
    public Guid Id { get; set; }
    public int ProductId { get; set; }
    public int SortOrder { get; set; }
    public string? AltText { get; set; }
    public Guid? FileId { get; set; }
    public string? ExternalUrl { get; set; }
    public Guid? Card600FileId { get; set; }
    public Guid? Detail1000FileId { get; set; }
    public string Card600Status { get; set; } = "pending";
    public string Detail1000Status { get; set; } = "pending";
    public string? Card600Error { get; set; }
    public string? Detail1000Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public static ProductImage CreateFile(int productId, int sortOrder, Guid fileId, string? altText, DateTime nowUtc)
    {
        if (productId <= 0 || sortOrder < 0 || fileId == Guid.Empty || nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Product image source is invalid.");
        return new ProductImage { Id = Guid.NewGuid(), ProductId = productId, SortOrder = sortOrder, FileId = fileId,
            AltText = NormalizeAltText(altText), CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
    }

    public static ProductImage CreateExternal(int productId, int sortOrder, string url, string? altText, DateTime nowUtc)
    {
        if (productId <= 0 || sortOrder < 0 || string.IsNullOrWhiteSpace(url) || url.Length > 2048 || nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Product image source is invalid.");
        return new ProductImage { Id = Guid.NewGuid(), ProductId = productId, SortOrder = sortOrder, ExternalUrl = url.Trim(),
            AltText = NormalizeAltText(altText), CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
    }

    public string SourceUrl => ExternalUrl ?? (FileId.HasValue ? $"/files/{FileId.Value:D}" : throw new InvalidOperationException("Product image has no source."));
    public string CardUrl => Card600FileId.HasValue ? $"/files/{Card600FileId.Value:D}" : SourceUrl;
    public string DetailUrl => Detail1000FileId.HasValue ? $"/files/{Detail1000FileId.Value:D}" : SourceUrl;
    private static string? NormalizeAltText(string? altText)
    {
        var normalized = altText?.Trim();
        if (normalized?.Length > 500) throw new ArgumentException("Alt text must be 500 characters or fewer.", nameof(altText));
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
public sealed class ProductOffer { public int Id { get; set; } public int ProductId { get; set; } public Product Product { get; set; } = null!; public int PartnerShopId { get; set; } public PartnerShop PartnerShop { get; set; } = null!; public int PriceVnd { get; set; } public string? Url { get; set; } public string? ImageUrl { get; set; } public int? OriginalPriceVnd { get; set; } public string? SpecificationsJson { get; set; } public string StockStatus { get; set; } = "unknown"; public DateTime UpdatedAtUtc { get; set; } public bool IsActive { get; set; } = true; public bool IsOutlier { get; set; } public string? OutlierReason { get; set; } }
public sealed class ProductOfferSnapshot { public long Id { get; set; } public int ProductOfferId { get; set; } public int PriceVnd { get; set; } public string? Url { get; set; } public string? ImageUrl { get; set; } public int? OriginalPriceVnd { get; set; } public string? SpecificationsJson { get; set; } public string StockStatus { get; set; } = "unknown"; public DateTime CapturedAtUtc { get; set; } }
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

public sealed class PriceHistoryPoint
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public DateTime DateUtc { get; set; }
    public int MinPriceVnd { get; set; }
    public int? PartnerShopId { get; set; }
    public PartnerShop? PartnerShop { get; set; }

    public static PriceHistoryPoint Create(int productId, DateTime dateUtc, int minPriceVnd, int? partnerShopId)
    {
        if (productId <= 0) throw new ArgumentOutOfRangeException(nameof(productId));
        if (dateUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("Price history dates must be UTC.", nameof(dateUtc));
        if (minPriceVnd < 0) throw new ArgumentOutOfRangeException(nameof(minPriceVnd));
        if (partnerShopId is <= 0) throw new ArgumentOutOfRangeException(nameof(partnerShopId));
        return new PriceHistoryPoint { ProductId = productId, DateUtc = dateUtc, MinPriceVnd = minPriceVnd, PartnerShopId = partnerShopId };
    }
}

public sealed record PublicPriceMinimum(int MinPriceVnd, int? PartnerShopId);

public static class CatalogOfferPolicy
{
    public const int FreshnessDays = 30;
}
