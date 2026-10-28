using CompareShopAPI.Application.Admin;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class MatchingCandidateRankerTests
{
    [Theory]
    [InlineData("CÀ-PHÊ 16GB/512GB", "ca phe 16gb 512gb")]
    [InlineData("cafe\u0301 CAFÉ", "cafe")]
    [InlineData("𐐀 １２３", "𐐨 １２３")]
    public void NormalizationRemovesMarksCaseAndPunctuationAndDeduplicates(string pending, string product)
    {
        var ranker = new MatchingCandidateRanker([new(1, product, "Café--APPLE")]);
        var result = Assert.Single(ranker.Rank(pending, "cafe apple"));
        Assert.Equal(1d, result.SimilarityScore);
        Assert.Equal(product, result.Name);
        Assert.Equal("Café--APPLE", result.Brand);
    }

    [Fact]
    public void JaccardUsesDistinctTokenUnion()
    {
        var ranker = new MatchingCandidateRanker([new(2, "air m5", null), new(1, "mac air mac", null)]);
        var result = ranker.Rank("mac air 16gb mac", null);
        Assert.Equal(new[] { 1, 2 }, result.Select(x => x.ProductId));
        Assert.Equal(2d / 3, result[0].SimilarityScore);
        Assert.Equal(1d / 4, result[1].SimilarityScore);
    }

    [Fact]
    public void ExactBrandLeadsMissingBrandThenKnownConflictRegardlessOfNameScore()
    {
        var ranker = new MatchingCandidateRanker([
            new(1, "mac air", "Other"), new(2, "mac", null),
            new(3, "unrelated", " ÁPPLE! "), new(4, "mac air", "apple")]);
        Assert.Equal(new[] { 4, 3, 2, 1 }, ranker.Rank("mac air", "Apple").Select(x => x.ProductId));
        Assert.Equal(new[] { 1, 4, 2, 3 }, ranker.Rank("mac air", null).Select(x => x.ProductId));
    }

    [Theory]
    [InlineData("", "anything")]
    [InlineData("anything", "---")]
    [InlineData("!!!", "")]
    public void EmptyTokenSetsScoreZero(string pending, string product)
    {
        Assert.Equal(0d, Assert.Single(new MatchingCandidateRanker([new(1, product, null)]).Rank(pending, "---")).SimilarityScore);
    }

    [Fact]
    public void ZeroScoresAreNotSuppressedAndTiesUseProductIdWithTopFiveLimit()
    {
        var products = Enumerable.Range(1, 8).Reverse().Select(id => new MatchingCandidateProduct(id, "unrelated", null)).ToArray();
        var result = new MatchingCandidateRanker(products).Rank("mac", null);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Select(x => x.ProductId));
        Assert.All(result, x => Assert.Equal(0d, x.SimilarityScore));
        Assert.Empty(new MatchingCandidateRanker([]).Rank("mac", "apple"));
    }
}
