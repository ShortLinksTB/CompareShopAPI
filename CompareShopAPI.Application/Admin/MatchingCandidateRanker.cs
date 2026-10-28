using System.Globalization;
using System.Text;

namespace CompareShopAPI.Application.Admin;

/// <summary>Advisory ranking only; selection and approval remain explicit admin actions.</summary>
public sealed class MatchingCandidateRanker
{
    private const int MaximumCandidates = 5;
    private readonly IReadOnlyList<PreparedProduct> products;

    public MatchingCandidateRanker(IReadOnlyList<MatchingCandidateProduct> products)
    {
        this.products = products.Select(x => new PreparedProduct(x, Tokens(x.Name), Normalize(x.Brand))).ToArray();
    }

    public IReadOnlyList<AdminMatchingCandidate> Rank(string name, string? brand)
    {
        var tokens = Tokens(name);
        var normalizedBrand = Normalize(brand);
        return products.Select(x => new
            {
                Product = x.Product,
                Tier = BrandTier(normalizedBrand, x.Brand),
                Score = Jaccard(tokens, x.Tokens)
            })
            .OrderBy(x => x.Tier).ThenByDescending(x => x.Score).ThenBy(x => x.Product.ProductId)
            .Take(MaximumCandidates)
            .Select(x => new AdminMatchingCandidate(x.Product.ProductId, x.Product.Name, x.Product.Brand, x.Score))
            .ToArray();
    }

    private static int BrandTier(string pending, string candidate) =>
        pending.Length == 0 || candidate.Length == 0 ? 1 : pending == candidate ? 0 : 2;

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        var intersection = left.Count(right.Contains);
        return (double)intersection / (left.Count + right.Count - intersection);
    }

    private static HashSet<string> Tokens(string value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string? value)
    {
        var result = new StringBuilder();
        foreach (var rune in (value ?? string.Empty).Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (Rune.IsLetterOrDigit(rune)) result.Append(Rune.ToLowerInvariant(rune).ToString());
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        return result.ToString().TrimEnd();
    }

    private sealed record PreparedProduct(MatchingCandidateProduct Product, HashSet<string> Tokens, string Brand);
}
