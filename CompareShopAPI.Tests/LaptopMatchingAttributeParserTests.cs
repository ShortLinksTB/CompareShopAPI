using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class LaptopMatchingAttributeParserTests
{
    [Fact]
    public void SpecificationsTakePrecedenceAndNameFillsMissingValues()
    {
        var result = LaptopMatchingAttributeParser.Parse("MacBook Air M3 16GB 512GB", "Apple", "{\"CPU\":\"Apple M2\",\"RAM\":\"24GB\",\"Storage\":\"1TB SSD\",\"Screen\":\"13.6 inch\"}");
        Assert.Equal("Apple M2", result.Chip);
        Assert.Equal(24, result.RamGb);
        Assert.Equal(1024, result.SsdGb);
        Assert.Equal("MacBook Air", result.Line);
        Assert.Equal("13.6 inch", result.Screen);
    }

    [Fact]
    public void MissingAttributesStayUnknownAndAccessoryIsExplicit()
    {
        var missing = LaptopMatchingAttributeParser.Parse("Unclassified device", null, "not-json");
        Assert.Null(missing.RamGb);
        Assert.Null(missing.SsdGb);
        Assert.Null(missing.IsAccessory);
        Assert.True(LaptopMatchingAttributeParser.Parse("USB-C 100W charger", null, null).IsAccessory);
        var listing = LaptopMatchingAttributeParser.Parse("Lenovo ThinkPad T14 16GB 1TB 15.6\"", "Lenovo", null);
        Assert.Equal(1024, listing.SsdGb);
        Assert.Equal("15.6 inch", listing.Screen);
    }

    [Fact]
    public void RankingUsesAttributeConflictsAndCapsAtFiveWithoutApproving()
    {
        var candidates = Enumerable.Range(1, 7).Select(i => new MatchingCandidateProduct(i, $"Lenovo ThinkPad T14 Gen 5 Core Ultra 7 32GB 1TB", "Lenovo")).ToArray();
        var ranker = new MatchingCandidateRanker(candidates);
        var result = ranker.Rank("Lenovo ThinkPad T14 Core Ultra 7 32GB 1TB", "Lenovo");
        Assert.Equal(5, result.Count);
        Assert.Equal(1, result[0].ProductId);
        Assert.All(result, x => Assert.Equal("confident", x.Level));
    }

    [Fact]
    public void LikelySkipVerdictSuppressesConfidentCandidateLevels()
    {
        var candidates = new[]
        {
            new AdminMatchingCandidate(1, "USB-C charger", null, 100) { Level = "confident" },
            new AdminMatchingCandidate(2, "Another charger", null, 70) { Level = "review" }
        };

        var guarded = MatchingCandidateRanker.ApplyVerdictGuard("likely_skip", candidates);

        Assert.Equal(new[] { "review", "review" }, guarded.Select(x => x.Level));
        Assert.Equal("confident", MatchingCandidateRanker.ApplyVerdictGuard("confident", candidates)[0].Level);
    }

    [Fact]
    public void SkipRequiresReasonCannotBeApprovedAndRestoreReturnsToPending()
    {
        var item = ProductMatching.ForSourceItem("Laptop", "Brand", DateTime.UtcNow, 1, "sku-1");
        item.Skip("duplicate", "same source", "reviewer", DateTime.UtcNow);
        Assert.Equal("skipped", item.Status);
        Assert.False(item.TryApprove(10));
        item.Restore();
        Assert.Equal("pending", item.Status);
        Assert.True(item.TryApprove(10));
        Assert.Equal("approved", item.Status);
    }
}
