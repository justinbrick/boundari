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
        game.AddNations(int.TryParse(Environment.GetEnvironmentVariable("BOUNDARI_NATIONS"), out int nations) ? nations : 120);
        game.AddBots(int.TryParse(Environment.GetEnvironmentVariable("BOUNDARI_BOTS"), out int n) ? n : 400);
        var timer = Stopwatch.StartNew();
        long worst = 0;
        var worstPart = new long[4];
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("BOUNDARI_MINUTES"), out int m) ? m : 5;
        for (int minute = 1; minute <= minutes; minute++)
        {
            for (int t = 0; t < 60 * Balance.TicksPerSecond; t++)
            {
                long start = timer.ElapsedTicks;
                var before = (long[])game.Profile.Clone();
                game.Step();
                worst = Math.Max(worst, timer.ElapsedTicks - start);
                for (int k = 0; k < 4; k++)
                    worstPart[k] = Math.Max(worstPart[k], game.Profile[k] - before[k]);
                game.ChangedTiles.Clear();
            }
            int owned = game.Owner.Count(o => o != Game.Nobody);
            var top = game.Players.OrderByDescending(p => p.TileCount).First();
            string Kind(PlayerKind k) => $"{game.Players.Count(p => p.Kind == k && p.IsAlive)} {k.ToString().ToLower()}s ({game.Players.Where(p => p.Kind == k).Sum(p => (long)p.TileCount) * 100 / Math.Max(1, owned)}% of land)";
            var built = game.Buildings.GroupBy(b => b.Type).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Sum(b => b.Level)}");
            _out.WriteLine($"  alive: {Kind(PlayerKind.Nation)}, {Kind(PlayerKind.Bot)}; buildings (levels): {string.Join(", ", built)}");
            _out.WriteLine($"minute {minute}: {100.0 * owned / map.LandTileCount:F0}% land owned, " +
                           $"{game.Players.Count(p => p.IsAlive)} alive, biggest {top.Name} {top.TileCount} tiles, " +
                           $"{game.Attacks.Count} attacks, {game.Buildings.Count(b => b.Type == BuildingType.Port)} ports, " +
                           $"{game.TradeShips.Count} trade ships, trade gold {game.Players.Sum(p => p.TradeGold):N0}, avg {timer.Elapsed.TotalMilliseconds / (minute * 600.0):F2} ms/tick, " +
                           $"worst {worst * 1000.0 / Stopwatch.Frequency:F1} ms | ms total: bots {game.Profile[0] * 1000 / Stopwatch.Frequency}, " +
                           $"econ {game.Profile[1] * 1000 / Stopwatch.Frequency}, trade {game.Profile[2] * 1000 / Stopwatch.Frequency}, attacks {game.Profile[3] * 1000 / Stopwatch.Frequency} | worst parts ms: " +
                           string.Join("/", worstPart.Select(x => x * 1000 / Stopwatch.Frequency)));
        }
    }
}

public class BoatBenchmark
{
    private readonly ITestOutputHelper _out;

    public BoatBenchmark(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EuropeLongBoat()
    {
        if (Environment.GetEnvironmentVariable("BOUNDARI_BENCH") != "1")
            return;
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Boundari.sln")) || !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir)!;
        GameMap map;
        using (var file = File.OpenRead(Path.Combine(dir, "src", "Boundari", "maps", "europe.bmap")))
            map = MapFile.Read(file);
        var geo = map.Geo!;
        int Tile(double lon, double lat) => map.Index((int)geo.TileX(lon, map.Width), (int)geo.TileY(lat, map.Height));

        var game = new Game(map, 1, spawnPhaseTicks: 1);
        var me = game.AddPlayer("Me");
        int brest = Tile(-4.3, 48.3);
        _out.WriteLine($"brest land {map.IsLand(brest)} coastal {game.IsCoastal(brest)}");
        game.Submit(new SpawnIntent(me.Id, brest));
        game.Step();
        game.Step();
        var ways = game.Waterways;
        int Sea(double lon, double lat) { int t = Tile(lon, lat); return map.IsWater(t) ? t : -1; }
        foreach (var (name, a, b) in new[] { ("Biscay-Irish Sea", Sea(-5, 47), Sea(-5.5, 53.5)), ("Biscay-Aegean", Sea(-5, 47), Sea(25, 38)),
                                             ("Biscay-Baltic", Sea(-5, 47), Sea(20, 58)), ("Biscay-Aegean again", Sea(-5, 47), Sea(25, 38)) })
        {
            var t0 = Stopwatch.StartNew();
            var path = a >= 0 && b >= 0 ? ways.FindPath(a, b) : null;
            _out.WriteLine($"path {name}: {path?.Length ?? -1} tiles, {t0.ElapsedMilliseconds} ms");
        }
        foreach (var (name, lon, lat) in new[] { ("Ireland", -8.0, 53.0), ("Iceland", -19.0, 64.8), ("Greece", 22.5, 39.5), ("Finland", 25.0, 62.0) })
        {
            var timer = Stopwatch.StartNew();
            game.Submit(new BoatIntent(me.Id, Tile(lon, lat), 5));
            game.Step();
            _out.WriteLine($"{name}: boats {game.Boats.Count}, path {(game.Boats.Count > 0 ? game.Boats[^1].Path.Length : 0)}, {timer.ElapsedMilliseconds} ms");
        }
    }
}
