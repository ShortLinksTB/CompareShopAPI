namespace CompareShopAPI.Domain.Admin;

public sealed class AffiliatePlatform
{
    private AffiliatePlatform() { }

    public AffiliatePlatform(string name, string normalizedName, string? homeUrl, string? notes, DateTime nowUtc)
    {
        Id = 0;
        Name = name;
        NormalizedName = normalizedName;
        HomeUrl = homeUrl;
        Notes = notes;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public string? HomeUrl { get; private set; }
    public string? Notes { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public void Update(string name, string normalizedName, string? homeUrl, string? notes, DateTime nowUtc)
    {
        Name = name;
        NormalizedName = normalizedName;
        HomeUrl = homeUrl;
        Notes = notes;
        UpdatedAtUtc = nowUtc;
    }
}
