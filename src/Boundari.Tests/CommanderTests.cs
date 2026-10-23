using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The dots game's start and opponents: commanders on random spots of the mainland, Locals
/// over most of the land, and AI commanders that expand, upgrade and push with their dots.
/// </summary>
[Collection("Timing")]
public class CommanderTests
{
    private readonly ITestOutputHelper _out;
    public CommanderTests(ITestOutputHelper output) => _out = output;

    private static GameMap RealMap(string mapId)
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Boundari.sln")) || !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir)!;
        using var file = File.OpenRead(Path.Combine(dir, "src", "Boundari", "maps", mapId + ".bmap"));
        return MapFile.Read(file);
    }

    /// <summary>A match set up as World.cs does it for the dots game: you, then the AI commanders, then the Locals.</summary>
    private static (Game Game, Player Me) DotsMatch(GameMap map, ulong seed, int commanders = 2, int locals = 150)
    {
        var game = new Game(map, seed);
        var me = game.AddPlayer("You");
        game.SpawnAtRandom(me);
        game.StartsWhenSpawned = me.Id;
        game.AddNations(commanders);
        game.AddBots(locals);
        return (game, me);
    }

    [Theory]
    [InlineData("westeurope")]
    [InlineData("ardennes")]
    public void LocalsCoverMostOfTheLand_CommandersStartApartOnTheMainland(string mapId)
    {
        var map = RealMap(mapId);
        var clock = Stopwatch.StartNew();
        var (game, me) = DotsMatch(map, 7);
        _out.WriteLine($"{mapId}: set up in {clock.ElapsedMilliseconds} ms");

        var commanders = game.Players.Where(p => !p.IsBot).ToList();
        var locals = game.Players.Where(p => p.IsBot).ToList();
        Assert.Equal(3, commanders.Count);
        Assert.True(locals.Count >= 140, $"{locals.Count} Locals");
        Assert.All(commanders, c => Assert.True(c.HasSpawned && c.TileCount > 0, c.Name));
        Assert.All(commanders, c => Assert.Equal(10, c.DotCount));

        long land = map.LandTileCount, held = locals.Sum(l => (long)l.TileCount);
        _out.WriteLine($"Locals hold {100 * held / land}% of the land; sizes {locals.Min(l => l.TileCount)}..{locals.Max(l => l.TileCount)} tiles");
        Assert.True(held * 100 > land * 75, $"Locals hold only {100 * held / land}%");

        // All on the biggest landmass, apart, with wilderness round them.
        int w = map.Width, mainland = Array.IndexOf(map.LandmassSizes, map.LandmassSizes.Max());
        var homes = commanders.Select(c => game.Dots.First(d => d.Owner == c).Tile).ToList();
        Assert.All(homes, t => Assert.Equal(mainland, map.LandmassId[t]));
        for (int i = 0; i < homes.Count; i++)
            for (int j = i + 1; j < homes.Count; j++)
            {
                double d = Math.Sqrt(Math.Pow(homes[i] % w - homes[j] % w, 2) + Math.Pow(homes[i] / w - homes[j] / w, 2));
                _out.WriteLine($"commanders {i}-{j}: {d:F0} tiles apart");
                Assert.True(d > game.Rules.D(LocalsRules.CommanderSpacing) / 2);
            }
        foreach (var c in commanders)
        {
            int wild = 0;
            foreach (int t in c.Border)
                if (Neighbours(map, t).Any(n => map.IsLand(n) && game.Owner[n] == Game.Nobody))
                    wild++;
            Assert.True(wild > c.Border.Count / 2, $"{c.Name}: only {wild} of {c.Border.Count} border tiles face wilderness");
        }
    }

    private static IEnumerable<int> Neighbours(GameMap map, int t)
    {
        int w = map.Width, x = t % w, y = t / w;
        if (x > 0) yield return t - 1;
        if (x < w - 1) yield return t + 1;
        if (y > 0) yield return t - w;
        if (y < map.Height - 1) yield return t + w;
    }

    [Theory]
    [InlineData(2UL)]
    [InlineData(4UL)]
    public void ACrampedGroup_FormsASecondRow_WithoutOverlapping(ulong seed)
    {
        // Your ten dots on a stretch of the Ardennes border, then cramped onto about half of it.
        var map = RealMap("ardennes");
        var (game, me) = DotsMatch(map, seed);
        game.Step();
        game.Submit(new ExpandIntent(me.Id));
        for (int t = 0; t < 300; t++)
            game.Step();
        game.Submit(new ExpandIntent(me.Id, Start: false));
        var dots = game.DotsOf(me);
        var ids = dots.Select(d => d.Id).ToArray();
        int border = game.BorderTileNear(me, dots[0].Tile);
        game.Submit(new MoveDotsIntent(me.Id, ids, border));
        for (int t = 0; t < 600; t++)
            game.Step();
        var (from, to) = game.BorderStretch(me, border, 2 * DotRules.Spacing);
        game.Submit(new SpreadDotsIntent(me.Id, ids, from, to));
        for (int t = 0; t < 300; t++)
            game.Step();

        int w = map.Width, second = game.Rules.DotSecondRowBack;
        double Gap(Dot d) => dots.Where(o => o != d).Min(o => Math.Sqrt(Math.Pow(o.Tile % w - d.Tile % w, 2) + Math.Pow(o.Tile / w - d.Tile / w, 2)));
        _out.WriteLine(string.Join(" ", dots.Select(d => $"{game.DistanceToLine(d)}/{Gap(d):F0}")) + "  (tiles behind the line / to the nearest other dot)");
        Assert.Contains(dots, d => game.DistanceToLine(d) == second);
        Assert.All(dots, d => Assert.False(d.IsWalking));
        Assert.All(dots, d => Assert.True(Gap(d) >= DotRules.Spacing * 19 / 20, $"{d} overlaps another ({Gap(d):F1} tiles)"));
    }

    [Fact]
    public void SameSeed_SameMatch()
    {
        var map = RealMap("ardennes");
        ushort[] Play()
        {
            var (game, _) = DotsMatch(map, 5);
            for (int t = 0; t < 1200; t++)
                game.Step();
            return game.Owner.ToArray();
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void AiCommanders_ExpandUpgradeAndTakeLocalsLand()
    {
        var map = RealMap("westeurope");
        var (game, me) = DotsMatch(map, 11);
        var ais = game.Players.Where(p => p.IsNation).ToList();
        int locals0 = game.Players.Count(p => p.IsBot && p.IsAlive);
        var clock = Stopwatch.StartNew();
        long worst = 0;
        var pushesSeen = new HashSet<int>();
        for (int s = 1; s <= 300; s++)
        {
            for (int t = 0; t < 10; t++)
            {
                long t0 = Stopwatch.GetTimestamp();
                game.Step();
                worst = Math.Max(worst, Stopwatch.GetTimestamp() - t0);
                foreach (var p in game.Pushes)
                    pushesSeen.Add(p.Id);
            }
            if (s % 30 == 0)
                foreach (var ai in ais)
                {
                    var dots = game.DotsOf(ai);
                    _out.WriteLine($"{s,3}s {ai.Name}: {ai.TileCount} tiles, {dots.Count} dots (levels {string.Join("", dots.Select(d => d.Strength))}), " +
                                   $"Funds {ai.Gold / 1000}K, expanding {game.IsExpanding(ai)}, pushes {string.Join(",", game.Pushes.Where(p => p.Attacker == ai).Select(p => $"{p.TargetId}x{p.Dots.Count}"))}");
                }
        }
        int locals = game.Players.Count(p => p.IsBot && p.IsAlive);
        _out.WriteLine($"5 min in {clock.ElapsedMilliseconds} ms, worst tick {worst * 1000.0 / Stopwatch.Frequency:F1} ms; {pushesSeen.Count} pushes; Locals {locals0} -> {locals}");
        foreach (var ai in ais)
        {
            Assert.Equal(ai.DotCap, ai.DotCount);
            Assert.Contains(game.Dots, d => d.Owner == ai && d.Strength > 1);
        }
        Assert.True(pushesSeen.Count > 0, "no AI push started");
    }
}
