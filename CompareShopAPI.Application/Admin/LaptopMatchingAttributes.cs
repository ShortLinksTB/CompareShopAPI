using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompareShopAPI.Application.Admin;

public sealed record LaptopAttributes(string? Brand, string? Line, string? Chip, string? Gpu, int? RamGb, int? SsdGb, string? Screen, bool? IsAccessory);

/// <summary>Deterministic laptop-only attribute extraction. Missing values remain unknown.</summary>
public static partial class LaptopMatchingAttributeParser
{
    [GeneratedRegex(@"(?<!\d)(\d{1,3})\s*(?:GB|G)(?![A-Z])", RegexOptions.IgnoreCase)] private static partial Regex Capacity();
    [GeneratedRegex("(?<!\\d)(\\d{1,2}(?:\\.\\d)?)\\s*(?:['\\\"”]|inch|inches|in)(?=\\s|$)", RegexOptions.IgnoreCase)] private static partial Regex ScreenSize();
    [GeneratedRegex(@"\b(?:RTX\s?\d{3,4}(?:\s?Ti|\s?SUPER)?|GTX\s?\d{3,4}(?:\s?Ti)?|Radeon\s?(?:RX\s?)?\d{3,4}M?)\b", RegexOptions.IgnoreCase)] private static partial Regex GpuToken();
    [GeneratedRegex(@"\b(?:Core\s?Ultra\s?[3579]\s?\d{1,3}[A-Z]{0,2}|Core\s?i[3579]-?\d{4,5}[A-Z]{0,2}|Ryzen\s?[3579]\s?\d{3,4}[A-Z]{0,2}|Apple\s?M[1-5](?:\s?(?:Pro|Max|Ultra))?)\b", RegexOptions.IgnoreCase)] private static partial Regex ChipToken();

    public static LaptopAttributes Parse(string? name, string? brand, string? specificationsJson)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(specificationsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(specificationsJson);
                Flatten(doc.RootElement, values);
            }
            catch (JsonException) { /* malformed source data falls back to listing text */ }
        }
        string? Spec(params string[] keys) => values.FirstOrDefault(x => keys.Any(k => NormalizeKey(x.Key).Contains(NormalizeKey(k), StringComparison.Ordinal))).Value;
        var text = string.Join(' ', new[] { name, brand }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var normalized = Fold(text);
        var effectiveBrand = Clean(brand) ?? Spec("brand", "hãng", "thuong hieu");
        effectiveBrand ??= new[] { "Apple", "ASUS", "Acer", "Lenovo", "Dell", "HP", "MSI", "Gigabyte", "LG", "Samsung", "Microsoft" }.FirstOrDefault(x => normalized.Contains(Fold(x), StringComparison.Ordinal));
        var storage = Spec("ssd", "storage", "dung luong o cung", "bo nho trong");
        var memory = Spec("ram", "memory", "bo nho");
        var screen = Spec("screen", "man hinh", "kich thuoc man hinh");
        var chip = Spec("cpu", "chip", "processor", "bo xu ly");
        var gpu = Spec("gpu", "graphics", "do hoa", "card man hinh");
        int? RamGb = ParseCapacity(memory);
        if (RamGb is null)
        {
            var ramMatch = Capacity().Matches(text).Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0).FirstOrDefault(n => n is >= 2 and <= 256);
            if (ramMatch > 0) RamGb = ramMatch;
        }
        int? SsdGb = ParseCapacity(storage) ?? ParseStorage(text);
        var screenMatch = ScreenSize().Match(text);
        var chipMatch = ChipToken().Match(text);
        var gpuMatch = GpuToken().Match(text);
        var screenValue = Clean(screen) ?? (screenMatch.Success ? screenMatch.Groups[1].Value + " inch" : null);
        var chipValue = Clean(chip) ?? (chipMatch.Success ? Normalize(chipMatch.Value) : null);
        var gpuValue = Clean(gpu) ?? (gpuMatch.Success ? Normalize(gpuMatch.Value) : null);
        var line = normalized.Contains("macbook air") ? "MacBook Air" : normalized.Contains("macbook pro") ? "MacBook Pro" : FindLine(normalized);
        bool? accessory = Regex.IsMatch(normalized, @"\b(charger|adapter|sac|case|tui|keyboard|ban phim|mouse|chuot|dock|hub|screen protector|mieng dan)\b") ? true
            : Regex.IsMatch(normalized, @"\b(laptop|notebook|macbook|thinkpad|vivobook|zenbook|gaming|ideapad|inspiron|pavilion)\b") ? false : null;
        return new(effectiveBrand, line, chipValue, gpuValue, RamGb, SsdGb, screenValue, accessory);
    }

    private static void Flatten(JsonElement e, Dictionary<string,string> target, string prefix = "")
    {
        if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Flatten(p.Value, target, prefix + " " + p.Name);
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Flatten(item, target, prefix);
        else if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())) target[prefix.Trim()] = e.GetString()!;
        else if (e.ValueKind == JsonValueKind.Number) target[prefix.Trim()] = e.ToString();
    }
    private static string NormalizeKey(string s) => Fold(s).Replace(" ", "");
    private static string Fold(string s) => string.Concat(s.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
    private static string Normalize(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : Normalize(s);
    private static int? ParseCapacity(string? s)
    {
        if (s is null) return null;
        var m = Regex.Match(s, @"(?<!\d)(\d{1,5})\s*(TB|GB|G)\b", RegexOptions.IgnoreCase);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var n)) return null;
        if (m.Groups[2].Value.Equals("TB", StringComparison.OrdinalIgnoreCase)) n *= 1024;
        return n is >= 2 and <= 65536 ? n : null;
    }
    private static int? ParseStorage(string s)
    {
        var terabytes = Regex.Match(s, @"(?<!\d)(\d{1,2})\s*TB\b", RegexOptions.IgnoreCase);
        if (terabytes.Success && int.TryParse(terabytes.Groups[1].Value, out var tb)) return tb * 1024;
        var gigabytes = Regex.Match(s, @"(?<!\d)(\d{3,5})\s*(GB|G)\b", RegexOptions.IgnoreCase);
        return gigabytes.Success && int.TryParse(gigabytes.Groups[1].Value, out var gb) ? gb : null;
    }
    private static string? FindLine(string s) => new[] { "ThinkPad", "IdeaPad", "Yoga", "Legion", "LOQ", "Inspiron", "XPS", "Latitude", "Pavilion", "Victus", "Envy", "Omen", "VivoBook", "ZenBook", "ROG", "TUF", "Swift", "Aspire", "Predator", "Katana", "Stealth", "Prestige", "Gram", "Surface Laptop" }.FirstOrDefault(x => s.Contains(Fold(x), StringComparison.Ordinal));
}
