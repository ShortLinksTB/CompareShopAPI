namespace CompareShopAPI.Application.Admin;

/// <summary>Attribute based advisory ranking. A suggestion never changes a mapping.</summary>
public sealed class MatchingCandidateRanker
{
    private const int MaximumCandidates = 5;
    private readonly IReadOnlyList<Prepared> products;

    public MatchingCandidateRanker(IReadOnlyList<MatchingCandidateProduct> products)
    {
        this.products = products.Select(p => new Prepared(p, p.Attributes ?? LaptopMatchingAttributeParser.Parse(p.Name, p.Brand, null))).ToArray();
    }

    public IReadOnlyList<AdminMatchingCandidate> Rank(string name, string? brand) => Rank(name, brand, null);

    public IReadOnlyList<AdminMatchingCandidate> Rank(string name, string? brand, string? specificationsJson)
    {
        return Rank(LaptopMatchingAttributeParser.Parse(name, brand, specificationsJson));
    }

    public IReadOnlyList<AdminMatchingCandidate> Rank(LaptopAttributes source)
    {
        var scored = products.Select(p => Score(source, p)).OrderByDescending(x => x.Score).ThenBy(x => x.Product.ProductId).Take(MaximumCandidates).ToArray();
        return scored.Select(x => new AdminMatchingCandidate(x.Product.ProductId, x.Product.Name, x.Product.Brand, x.Score)
        { Level = x.Level, Differences = x.Differences }).ToArray();
    }

    public static string Verdict(LaptopAttributes a, IReadOnlyList<AdminMatchingCandidate> candidates)
    {
        if (a.IsAccessory == true) return "likely_skip";
        if (candidates.Count == 0 || candidates[0].Score < 25) return "likely_new";
        if (candidates[0].Level == "confident") return "confident";
        return "review";
    }

    public static IReadOnlyList<AdminMatchingCandidate> ApplyVerdictGuard(string verdict, IReadOnlyList<AdminMatchingCandidate> candidates) =>
        verdict == "likely_skip"
            ? candidates.Select(candidate => candidate.Level == "confident" ? candidate with { Level = "review" } : candidate).ToArray()
            : candidates;

    private static Ranked Score(LaptopAttributes source, Prepared candidate)
    {
        var target = candidate.Attributes;
        var diffs = new List<string>();
        var points = 0d; var possible = 0d; var conflicts = 0;
        Compare(source.Brand, target.Brand, 15, "brand", ref points, ref possible, ref conflicts, diffs);
        Compare(source.Line, target.Line, 15, "line", ref points, ref possible, ref conflicts, diffs);
        Compare(source.Chip, target.Chip, 15, "chip", ref points, ref possible, ref conflicts, diffs);
        Compare(source.Gpu, target.Gpu, 12, "gpu", ref points, ref possible, ref conflicts, diffs);
        Compare(source.RamGb, target.RamGb, 12, "ram", ref points, ref possible, ref conflicts, diffs);
        Compare(source.SsdGb, target.SsdGb, 12, "ssd", ref points, ref possible, ref conflicts, diffs);
        Compare(source.Screen, target.Screen, 9, "screen", ref points, ref possible, ref conflicts, diffs);
        Compare(source.IsAccessory, target.IsAccessory, 10, "accessory", ref points, ref possible, ref conflicts, diffs);
        // Names can weakly rank listings whose structured data is sparse, but cannot make them confident.
        var nameScore = TokenSimilarity(source, candidate.Product.Name);
        var score = possible == 0 ? nameScore * 20 : Math.Clamp(points / possible * 100 + nameScore * 8, 0, 100);
        var level = conflicts == 0 && possible >= 60 && score >= 78 ? "confident" : conflicts > 0 ? "variant" : "review";
        return new(candidate.Product, Math.Round(score, 2), level, diffs);
    }

    private static void Compare<T>(T? a, T? b, double weight, string key, ref double points, ref double possible, ref int conflicts, List<string> diffs)
    {
        if (a is null || b is null) return;
        possible += weight;
        if (EqualityComparer<T>.Default.Equals(a, b) || a is string left && b is string right && Normalize(left) == Normalize(right)) points += weight;
        else { conflicts++; diffs.Add(key); }
    }

    private static double TokenSimilarity(LaptopAttributes source, string name)
    {
        var terms = new[] { source.Brand, source.Line, source.Chip, source.Gpu }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (terms.Length == 0) return 0;
        var text = Normalize(name);
        return (double)terms.Count(x => text.Contains(Normalize(x!), StringComparison.Ordinal)) / terms.Length;
    }

    private static string Normalize(string s) => string.Concat(s.Normalize(System.Text.NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
    private sealed record Prepared(MatchingCandidateProduct Product, LaptopAttributes Attributes);
    private sealed record Ranked(MatchingCandidateProduct Product, double Score, string Level, IReadOnlyList<string> Differences);
}
