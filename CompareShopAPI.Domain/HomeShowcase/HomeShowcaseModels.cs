namespace CompareShopAPI.Domain.HomeShowcase;

public enum HomeShowcaseSection { Featured, InterestedComputers }
public enum HomeShowcaseSnapshot { Draft, Published }
public sealed record HomeShowcaseSlotInput(int SlotIndex, int? ProductId);

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
    public static IReadOnlyList<int?> ValidateSlots(IReadOnlyList<HomeShowcaseSlotInput?>? slots, string field)
    {
        if (slots is null || slots.Count != SlotCount)
            throw new ArgumentException($"{field} must contain exactly {SlotCount} slot records.", field);

        var byIndex = new HomeShowcaseSlotInput?[SlotCount];
        for (var position = 0; position < slots.Count; position++)
        {
            var slot = slots[position];
            if (slot is null)
                throw new ArgumentException("Slot record must not be null.", $"{field}[{position + 1}]");
            if (slot.SlotIndex is < 1 or > SlotCount)
                throw new ArgumentException($"Slot index {slot.SlotIndex} must be between 1 and {SlotCount}.", $"{field}[{slot.SlotIndex}].slotIndex");
            if (byIndex[slot.SlotIndex - 1] is not null)
                throw new ArgumentException($"Slot index {slot.SlotIndex} is duplicated.", $"{field}[{slot.SlotIndex}].slotIndex");
            if (slot.ProductId is <= 0)
                throw new ArgumentException("Product IDs must be positive or null.", $"{field}[{slot.SlotIndex}].productId");
            byIndex[slot.SlotIndex - 1] = slot;
        }

        var missing = Array.FindIndex(byIndex, slot => slot is null);
        if (missing >= 0)
            throw new ArgumentException($"Slot index {missing + 1} is missing.", $"{field}[{missing + 1}].slotIndex");
        var productIds = byIndex.Select(slot => slot!.ProductId).ToArray();
        if (productIds.Where(id => id.HasValue).GroupBy(id => id).Any(group => group.Count() > 1))
            throw new ArgumentException($"{field} cannot contain a product more than once.", field);
        return productIds;
    }
}
