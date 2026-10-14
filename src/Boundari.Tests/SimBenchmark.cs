using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Opt-in: a full bot match on the real Europe map, reporting speed and outcome.
/// Run with BOUNDARI_BENCH=1 dotnet test --filter SimBenchmark
/// </summary>
public class SimBenchmark
{
    private readonly ITestOutputHelper _out;

    public SimBenchmark(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EuropeBotMatch()
    {
        if (Environment.GetEnvironmentVariable("BOUNDARI_BENCH") != "1")
            return;

        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Boundari.sln")) || !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir)!;
        GameMap map;
        using (var file = File.OpenRead(Path.Combine(dir, "src", "Boundari", "maps", "europe.bmap")))
            map = MapFile.Read(file);

        var game = new Game(map, 42, spawnPhaseTicks: 1);
        game.AddBots(40);
        var timer = Stopwatch.StartNew();
        long worst = 0;
        for (int minute = 1; minute <= 5; minute++)
        {
            for (int t = 0; t < 60 * Balance.TicksPerSecond; t++)
            {
                long start = timer.ElapsedTicks;
                game.Step();
                worst = Math.Max(worst, timer.ElapsedTicks - start);
                game.ChangedTiles.Clear();
            }
            int owned = game.Owner.Count(o => o != Game.Nobody);
            var top = game.Players.OrderByDescending(p => p.TileCount).First();
            _out.WriteLine($"minute {minute}: {100.0 * owned / map.LandTileCount:F0}% land owned, " +
                           $"{game.Players.Count(p => p.IsAlive)} alive, biggest {top.Name} {top.TileCount} tiles, " +
                           $"{game.Attacks.Count} attacks, avg {timer.Elapsed.TotalMilliseconds / (minute * 600):F2} ms/tick, " +
                           $"worst {worst * 1000.0 / Stopwatch.Frequency:F1} ms");
        }
    }
}
