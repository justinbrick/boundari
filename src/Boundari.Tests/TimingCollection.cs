using Xunit;

/// <summary>Tests that measure tick times run alone, after the rest, so other tests don't skew them.</summary>
[CollectionDefinition("Timing", DisableParallelization = true)]
public class TimingCollection
{
}
