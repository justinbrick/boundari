using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>Something that happened in the match, worth telling a player about.</summary>
public enum GameEventKind : byte
{
    AllianceRequested,      // A asked B
    AllianceAccepted,       // B accepted A's request
    AllianceRejected,       // B turned down A's request
    AllianceRequestExpired, // A's request to B ran out unanswered
    AllianceExpired,        // A and B's alliance ran its course
    AllianceBroken,         // A broke the alliance with B
    AllianceEnding,         // A and B's alliance has 30 s left (it can be renewed now)
    AllianceRenewRequested, // A asked B to renew their alliance
    AllianceRenewed,        // A's agreement renewed the alliance with B
    AttackStarted,          // A attacked B (a new attack, not more troops on one already going)
    BoatLaunched,           // A sent a boat towards B's coast
    NukeLaunched,           // A launched a bomb (Detail = NukeType) at B's land
    Conquered,              // A finished B off
    EmbargoStarted,         // A stopped trading with B
    EmbargoStopped,         // A resumed trading with B
    Targeted,               // A marked B as a target (also told to A's allies)
    QuickChat,              // A sent B a quick-chat phrase (Detail = index into ChatRules.Phrases)
    GoldEarned,             // A earned Detail gold at Tile (trade, trains), or from conquering B (Tile -1)
}

/// <summary>An event: who did it (A), to whom (B), any extra detail, and where (a tile, or -1).</summary>
public readonly record struct GameEvent(GameEventKind Kind, ushort A, ushort B, long Detail = 0, int Tile = -1);

public sealed partial class Game
{
    private readonly List<GameEvent> _events = new();

    /// <summary>
    /// The player whose events are recorded (the one at this screen), or Nobody for none.
    /// Events are output only: recording them never changes the game.
    /// </summary>
    public ushort EventsFor { get; set; } = Nobody;

    /// <summary>Events involving <see cref="EventsFor"/> since the reader last cleared this list.</summary>
    public List<GameEvent> Events => _events;

    private void Report(GameEventKind kind, Player a, Player? b, long detail = 0, int tile = -1)
    {
        ushort bId = b?.Id ?? Nobody;
        if (EventsFor != Nobody && (a.Id == EventsFor || bId == EventsFor))
            _events.Add(new GameEvent(kind, a.Id, bId, detail, tile));
    }

    /// <summary>Gold income worth a "+gold" pop-up for the watched player (only they are told).</summary>
    private void ReportGold(Player earner, long gold, int tile, Player? from = null)
    {
        if (gold > 0 && earner.Id == EventsFor)
            _events.Add(new GameEvent(GameEventKind.GoldEarned, earner.Id, from?.Id ?? Nobody, gold, tile));
    }
}
