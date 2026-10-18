using CompareShopAPI.Domain.Ingestion;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class IngestionDomainTests
{
    [Fact]
    public void SourceIsDueImmediatelyAndAgainAfterSeventyTwoHours()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var source = new DataSource("bhx", "Bách Hóa Xanh", "/bia", null, now);
        Assert.True(source.IsDue(now));
        source.Complete(now);
        Assert.False(source.IsDue(now.AddHours(71)));
        Assert.True(source.IsDue(now.AddHours(72)));
    }

    [Fact]
    public void JobCanOnlyFinishOnceAndSanitizesErrorDetails()
    {
        var now = DateTime.UtcNow;
        var job = new SyncJob(1, now);
        Assert.True(job.Start(now, "lease"));
        job.Finish(SyncJobState.Failed, now.AddMinutes(1), 0, 0, 0, "source_failure", "Bad\r\npage content");
        Assert.Equal(SyncJobState.Failed, job.State);
        Assert.Equal("Badpage content", job.ErrorSummary);
        Assert.Throws<InvalidOperationException>(() => job.Finish(SyncJobState.Success, now.AddMinutes(2), 1, 1, 0));
    }

    [Fact]
    public void SourceRejectsUnvalidatedCategory()
    {
        Assert.Throws<ArgumentException>(() => new DataSource("bhx", "Bách Hóa Xanh", "/fresh-food", null, DateTime.UtcNow));
    }

    [Fact]
    public void RetryAndMappingTransitionsEnforceTheirInvariants()
    {
        var now = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var source = new DataSource("bhx", "BHX", "/bia", null, now);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.AttachPartnerShop(0));
        source.AttachPartnerShop(4);
        source.ScheduleFailureRetry(now);
        Assert.False(source.IsDue(now.AddHours(5)));
        Assert.True(source.IsDue(now.AddHours(6)));
        source.MarkError();
        Assert.False(source.IsDue(now.AddDays(4)));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SyncJob(0, now));
        var mapping = new SourceItemMapping(1, "sku", new string('n', 305), new string('b', 205));
        Assert.Equal(300, mapping.Name.Length);
        Assert.Equal(200, mapping.Brand!.Length);
        Assert.False(mapping.IsApproved);
        mapping.UpdateSnapshot("updated", null);
        Assert.Null(mapping.Brand);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.SetOffer(0));
        mapping.Approve(2);
        Assert.True(mapping.IsApproved);
        Assert.Throws<InvalidOperationException>(() => mapping.Approve(3));
    }
}
