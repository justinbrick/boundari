using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class AllianceTests
{
    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>A small map with A owning the west half of the biggest landmass and B (a person, or a tribe) the east.</summary>
    private static (Game Game, Player A, Player B) Setup(PlayerKind bKind = PlayerKind.Human)
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2) { TroopAttacks = true, BoatsAllowed = true };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B", bKind);
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        Wait(game, 4);
        int midX = (int)land.Select(t => t % map.Width).Average();
        foreach (int t in land)
            SetOwnerMethod.Invoke(game, new object[] { t, t % map.Width >= midX ? b.Id : a.Id });
        return (game, a, b);
    }

    private static void Wait(Game game, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            game.Step();
    }

    [Fact]
    public void Request_ThenAskingBack_MakesAnAlliance_AndAlliesCantAttack()
    {
        var (game, a, b) = Setup();
        game.Submit(new AllianceIntent(a.Id, b.Id));
        Wait(game, 1);
        Assert.NotNull(game.PendingRequest(a, b));
        Assert.False(a.IsAlliedWith(b));

        game.Submit(new AllianceIntent(b.Id, a.Id)); // b says yes
        Wait(game, 1);
        Assert.True(a.IsAlliedWith(b) && b.IsAlliedWith(a));
        Assert.Empty(game.AllianceRequests);
        Assert.Equal(Relation.Friendly, game.RelationOf(a, b));

        int bTiles = b.TileCount;
        game.Submit(new AttackIntent(a.Id, b.Id, 100));
        Wait(game, 30);
        Assert.Empty(game.Attacks.Where(x => x.Attacker == a));
        Assert.Equal(bTiles, b.TileCount);
    }

    [Fact]
    public void Alliance_CanBeRenewed_OnlyInItsLast30Seconds_OnceBothAsk()
    {
        var (game, a, b) = Setup();
        game.Submit(new AllianceIntent(a.Id, b.Id));
        game.Submit(new AllianceIntent(b.Id, a.Id));
        Wait(game, 2);
        var alliance = game.AllianceBetween(a, b)!;
        int expires = alliance.ExpiresTick;

        Assert.False(game.CanRenewAlliance(a, b)); // too early
        game.Submit(new AllianceIntent(a.Id, b.Id));
        Wait(game, 1);
        Assert.False(alliance.AgreedToRenew(a));

        Wait(game, expires - AllianceRules.RenewWindowTicks - game.Tick);
        Assert.True(game.CanRenewAlliance(a, b));
        game.Submit(new AllianceIntent(a.Id, b.Id));
        Wait(game, 1);
        Assert.True(alliance.AgreedToRenew(a));
        Assert.False(game.CanRenewAlliance(a, b)); // already asked
        Assert.Equal(expires, alliance.ExpiresTick);

        game.Submit(new AllianceIntent(b.Id, a.Id));
        Wait(game, 1);
        Assert.Equal(game.Tick - 1 + AllianceRules.DurationTicks, alliance.ExpiresTick);
        Assert.False(alliance.AgreedToRenew(a) || alliance.AgreedToRenew(b));
    }

    [Fact]
    public void Embargo_StopsTrade_AndMakesANationDistrustful_UntilLifted()
    {
        var (game, a, b) = Setup(PlayerKind.Nation);
        Assert.True(game.CanTrade(a, b));
        Assert.Equal(Relation.Neutral, game.RelationOf(b, a));

        game.Submit(new EmbargoIntent(a.Id, b.Id, true));
        Wait(game, 1);
        Assert.True(game.HasEmbargo(a, b));
        Assert.False(game.CanTrade(a, b) || game.CanTrade(b, a)); // either way
        Assert.Equal(Relation.Distrustful, game.RelationOf(b, a));

        game.Submit(new EmbargoIntent(a.Id, b.Id, false));
        Wait(game, 1);
        Assert.True(game.CanTrade(a, b));
        Assert.Equal(Relation.Neutral, game.RelationOf(b, a));
    }

    [Fact]
    public void Target_MarksForTenSeconds_CostsRelation_AndHasACooldown()
    {
        var (game, a, b) = Setup();
        game.Submit(new TargetIntent(a.Id, b.Id));
        Wait(game, 1);
        Assert.True(game.IsMarkedFor(a, b));
        Assert.Equal(Relation.Distrustful, game.RelationOf(b, a));
        Assert.False(game.CanTarget(a, b)); // cooling down

        Wait(game, EmbargoRules.TargetDurationTicks);
        Assert.False(game.IsMarkedFor(a, b)); // run out
        Assert.False(game.CanTarget(a, b));
        Wait(game, EmbargoRules.TargetCooldownTicks - EmbargoRules.TargetDurationTicks);
        Assert.True(game.CanTarget(a, b));
    }

    [Fact]
    public void Emojis_ShowForFiveSeconds_AreRateLimited_AndANationAnswersAnInsult()
    {
        var (game, a, b) = Setup(PlayerKind.Nation);
        game.Submit(new EmojiIntent(a.Id, b.Id, Emoji.MiddleFinger));
        Wait(game, 1);
        Assert.Equal(Emoji.MiddleFinger, game.ShownEmoji(a, b));
        Assert.Null(game.ShownEmoji(a, a)); // only the recipient sees it
        Assert.Equal(Relation.Hostile, game.RelationOf(b, a));
        Assert.Null(game.ShownEmoji(b, a)); // the nation takes a moment...
        Wait(game, ChatRules.ReplyMaxTicks);
        Assert.NotNull(game.ShownEmoji(b, a)); // ...then answers

        for (int i = 0; i < ChatRules.EmojiLimit; i++)
            game.Submit(new EmojiIntent(a.Id, b.Id, Emoji.Grin));
        Wait(game, 1);
        Assert.False(game.CanSendEmoji(a, b.Id)); // five in five seconds is the limit
        Assert.True(game.CanSendEmoji(a, Game.Nobody)); // everyone counts separately

        Wait(game, ChatRules.EmojiShownTicks);
        Assert.Null(game.ShownEmoji(a, b));
        Assert.True(game.CanSendEmoji(a, b.Id));
    }

    [Fact]
    public void QuickChat_ReachesTheRecipient_WithACooldown()
    {
        var (game, a, b) = Setup();
        game.EventsFor = b.Id;
        game.Submit(new QuickChatIntent(a.Id, b.Id, 9)); // "Sorry"
        Wait(game, 1);
        Assert.Equal(new[] { new GameEvent(GameEventKind.QuickChat, a.Id, b.Id, 9) }, game.Events);
        Assert.False(game.CanQuickChat(a, b));
        Wait(game, ChatRules.QuickChatCooldownTicks);
        Assert.True(game.CanQuickChat(a, b));
    }

    [Fact]
    public void Events_AreRecordedOnlyForTheWatchedPlayer()
    {
        var (game, a, b) = Setup();
        game.Submit(new AllianceIntent(b.Id, a.Id));
        Wait(game, 1);
        Assert.Empty(game.Events); // nobody watched yet

        game.EventsFor = a.Id;
        game.Submit(new AllianceIntent(a.Id, b.Id)); // accepts b's request
        Wait(game, 1);
        Assert.Equal(new[] { new GameEvent(GameEventKind.AllianceAccepted, b.Id, a.Id) }, game.Events);

        game.Events.Clear();
        game.Submit(new BreakAllianceIntent(b.Id, a.Id));
        game.Submit(new AttackIntent(b.Id, a.Id, 50));
        Wait(game, 1);
        Assert.Equal(new[] { GameEventKind.AllianceBroken, GameEventKind.AttackStarted }, game.Events.Select(e => e.Kind));
        Assert.All(game.Events, e => Assert.Equal((b.Id, a.Id), (e.A, e.B)));
    }

    [Fact]
    public void Request_LapsesAfter20Seconds_AndCantBeRepeatedFor30()
    {
        var (game, a, b) = Setup();
        game.Submit(new AllianceIntent(a.Id, b.Id));
        Wait(game, AllianceRules.RequestTicks + 2);
        Assert.Null(game.PendingRequest(a, b));
        Assert.False(game.CanRequestAlliance(a, b)); // still cooling down (30 s from the request)
        Wait(game, AllianceRules.RequestCooldownTicks - AllianceRules.RequestTicks);
        Assert.True(game.CanRequestAlliance(a, b));
    }

    [Fact]
    public void Tribes_AcceptEveryRequest()
    {
        var (game, a, tribe) = Setup(PlayerKind.Bot);
        game.Submit(new AllianceIntent(a.Id, tribe.Id));
        Wait(game, 2);
        Assert.True(a.IsAlliedWith(tribe));
    }

    [Fact]
    public void Breaking_MakesATraitor_ForThirtySeconds()
    {
        var (game, a, b) = Setup();
        game.Submit(new AllianceIntent(a.Id, b.Id));
        game.Submit(new AllianceIntent(b.Id, a.Id));
        Wait(game, 1);
        Assert.True(a.IsAlliedWith(b));

        game.Submit(new BreakAllianceIntent(a.Id, b.Id));
        Wait(game, 1);
        Assert.False(a.IsAlliedWith(b));
        Assert.True(game.IsTraitor(a));
        Assert.False(game.IsTraitor(b));
        Assert.True(game.RelationValue(b, a) <= 0); // from +100 (allied) down by 100, as in OpenFront
        Wait(game, AllianceRules.TraitorTicks);
        Assert.False(game.IsTraitor(a));
    }

    [Fact]
    public void Alliances_ExpireAfterFiveMinutes()
    {
        var (game, a, b) = Setup();
        game.Submit(new AllianceIntent(a.Id, b.Id));
        game.Submit(new AllianceIntent(b.Id, a.Id));
        Wait(game, 1);
        Assert.True(a.IsAlliedWith(b));
        Wait(game, AllianceRules.DurationTicks + 1);
        Assert.False(a.IsAlliedWith(b));
        Assert.False(game.IsTraitor(a) || game.IsTraitor(b)); // running out isn't betrayal
    }

    [Fact]
    public void Attacking_AngersTheTarget_AndWithdrawsTheirRequest()
    {
        var (game, a, b) = Setup();
        game.Submit(new AllianceIntent(b.Id, a.Id));
        Wait(game, 1);
        Assert.NotNull(game.PendingRequest(b, a));
        game.Submit(new AttackIntent(a.Id, b.Id, 20));
        Wait(game, 1);
        Assert.Null(game.PendingRequest(b, a));
        Assert.Equal(Relation.Hostile, game.RelationOf(b, a)); // Medium: -70
    }

    [Fact]
    public void AnAttackUnderway_PullsBack_WhenTheSidesAlly()
    {
        var (game, a, b) = Setup();
        game.Submit(new AttackIntent(a.Id, b.Id, 50));
        Wait(game, 3);
        Assert.Contains(game.Attacks, x => x.Attacker == a);
        game.Submit(new AllianceIntent(a.Id, b.Id));
        game.Submit(new AllianceIntent(b.Id, a.Id));
        Wait(game, 2);
        Assert.DoesNotContain(game.Attacks, x => x.Attacker == a);
    }

    [Fact]
    public void Nations_FormAlliances_InAEuropeSizedMatch()
    {
        var map = MapGenerator.Generate(5, new MapGenSettings { Width = 600, Height = 400 });
        var game = new Game(map, 7, spawnPhaseTicks: 2) { TroopAttacks = true, BoatsAllowed = true };
        game.AddNations(20);
        game.AddBots(40);
        int most = 0;
        for (int t = 0; t < 2500; t++)
        {
            game.Step();
            most = System.Math.Max(most, game.Alliances.Count);
        }
        Assert.True(most > 0, "nations should ally with each other at some point");
    }
}
