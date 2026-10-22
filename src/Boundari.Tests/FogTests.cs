using System;
using Boundari.Sim;
using Xunit;

/// <summary>Light fog of war: how other players' troops are rounded and when further players' refresh.</summary>
public class FogTests
{
    [Theory]
    [InlineData(0L, 0L, 0L)]
    [InlineData(47_300L, 45_000L, 50_000L)]
    [InlineData(123_456L, 120_000L, 150_000L)]
    [InlineData(96_000L, 100_000L, 100_000L)]
    [InlineData(612_000L, 600_000L, 700_000L)]
    [InlineData(2_600_000L, 2_500_000L, 3_000_000L)]
    public void Approximate_RoundsToSteps(long troops, long neighbour, long far)
    {
        Assert.Equal(neighbour, FogRules.Approximate(troops, near: true));
        Assert.Equal(far, FogRules.Approximate(troops, near: false));
    }

    [Fact]
    public void Neighbours_AreRoundedCloser_ThanFurtherPlayers()
    {
        double nearWorst = 0, farWorst = 0;
        for (long troops = 1_000; troops < 5_000_000; troops = troops * 103 / 100)
        {
            nearWorst = Math.Max(nearWorst, Error(troops, true));
            farWorst = Math.Max(farWorst, Error(troops, false));
        }
        Assert.True(nearWorst <= 0.12, $"neighbours off by up to {nearWorst:P0}");
        Assert.True(farWorst <= 0.25, $"further players off by up to {farWorst:P0}");
        Assert.True(farWorst > nearWorst * 1.5);

        static double Error(long troops, bool near) => Math.Abs(FogRules.Approximate(troops, near) - troops) / (double)troops;
    }

    [Fact]
    public void FurtherPlayers_RefreshEvery20Seconds_OffsetByPlayer()
    {
        const ushort id = 5;
        int refreshes = 0;
        int last = FogRules.LastFarRefresh(id, 0);
        for (int tick = 1; tick <= 3 * FogRules.FarRefreshTicks; tick++)
        {
            int now = FogRules.LastFarRefresh(id, tick);
            Assert.True(now <= tick && tick - now < FogRules.FarRefreshTicks);
            if (now != last)
            {
                refreshes++;
                Assert.Equal(tick, now);
                Assert.Equal(0, FogRules.FarCycle(id, tick), 6); // the ring starts empty on a refresh
                last = now;
            }
            else
                Assert.True(FogRules.FarCycle(id, tick) > 0);
        }
        Assert.Equal(3, refreshes);
        Assert.NotEqual(FogRules.LastFarRefresh(1, 1000), FogRules.LastFarRefresh(2, 1000));
    }
}
