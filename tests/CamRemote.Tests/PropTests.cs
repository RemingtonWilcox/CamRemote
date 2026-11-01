using Xunit;

namespace CamRemote.Tests;

public class PropTests
{
    [Fact]
    public void A_list_property_offers_its_candidates()
    {
        var p = new Prop { Type = 0x0003, Candidates = [1, 2, 3] };
        Assert.Equal([1L, 2L, 3L], p.Choices());
    }

    [Fact]
    public void A_range_property_offers_every_step()
    {
        var p = new Prop { Type = 0x4002, Candidates = [10, 20, 5] };
        Assert.True(p.IsRange);
        Assert.Equal([10L, 15L, 20L], p.Choices());
    }

    [Theory]
    [InlineData(new long[] { 0, 10 })] // no step
    [InlineData(new long[] { 0, 10, 0 })] // zero step
    [InlineData(new long[] { 0, 100000, 1 })] // far too many to list
    public void A_range_that_cannot_be_listed_offers_nothing(long[] candidates)
    {
        Assert.Empty(new Prop { Type = 0x4002, Candidates = candidates }.Choices());
    }
}
