namespace CompareShopAPI.Domain.Catalog;

public sealed class Category { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public bool IsActive { get; set; } = true; }
public sealed class PartnerShop { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public string? WebsiteUrl { get; set; } public bool IsActive { get; set; } = true; }
public sealed class Product { public int Id { get; set; } public required string Slug { get; set; } public required string Name { get; set; } public string? Brand { get; set; } public string? Description { get; set; } public int CategoryId { get; set; } public Category Category { get; set; } = null!; public bool IsActive { get; set; } = true; public List<ProductOffer> Offers { get; set; } = []; public List<SpecificationGroup> SpecificationGroups { get; set; } = []; }
public sealed class ProductOffer { public int Id { get; set; } public int ProductId { get; set; } public Product Product { get; set; } = null!; public int PartnerShopId { get; set; } public PartnerShop PartnerShop { get; set; } = null!; public int PriceVnd { get; set; } public string? Url { get; set; } public DateTime UpdatedAtUtc { get; set; } public bool IsActive { get; set; } = true; public bool IsOutlier { get; set; } }
public sealed class SpecificationGroup { public int Id { get; set; } public int ProductId { get; set; } public Product Product { get; set; } = null!; public required string Name { get; set; } public int SortOrder { get; set; } public List<SpecificationItem> Items { get; set; } = []; }
public sealed class SpecificationItem { public int Id { get; set; } public int SpecificationGroupId { get; set; } public SpecificationGroup Group { get; set; } = null!; public required string Name { get; set; } public required string Value { get; set; } public int SortOrder { get; set; } }

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
