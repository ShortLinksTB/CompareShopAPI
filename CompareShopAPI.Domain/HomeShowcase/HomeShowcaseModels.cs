namespace CompareShopAPI.Domain.HomeShowcase;

public enum HomeShowcaseSection { Featured, InterestedComputers }
public enum HomeShowcaseSnapshot { Draft, Published }

public sealed class HomeShowcaseSlot
{
    public int Id { get; set; }
    public HomeShowcaseSnapshot Snapshot { get; set; }
    public HomeShowcaseSection Section { get; set; }
    public int SlotIndex { get; set; }
    public int? ProductId { get; set; }
}

public sealed class HomeShowcasePreviewToken
{
    public int Id { get; set; }
    public required string TokenHash { get; set; }
    public required string ActorSubject { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}

public static class HomeShowcaseRules
{
    public const int SlotCount = 15;
    public static IReadOnlyList<int?> ValidateSlots(IReadOnlyList<int?>? slots, string field)
    {
        if (slots is null || slots.Count != SlotCount)
            throw new ArgumentException($"{field} must contain exactly {SlotCount} slots.", field);
        if (slots.Any(id => id is <= 0)) throw new ArgumentException($"{field} product IDs must be positive or null.", field);
        var duplicates = slots.Where(id => id.HasValue).GroupBy(id => id).Any(group => group.Count() > 1);
        if (duplicates) throw new ArgumentException($"{field} cannot contain a product more than once.", field);
        return slots.ToArray();
    }
}
