using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// OpenFront's nation emojis: the moods a nation shows, each a short list of emojis it
/// picks from at random. OpenFront has a few hundred emojis, we have 16, so each mood is
/// mapped onto the nearest of ours (comments show OpenFront's originals).
/// </summary>
internal static class NationMoods
{
    public static readonly Emoji[] AssistAccept = { Emoji.Handshake };                 // 👍 🤝 🎯
    public static readonly Emoji[] AssistRelationTooLow = { Emoji.Question };          // 🥱 🤦
    public static readonly Emoji[] AssistTargetMe = { Emoji.Crying };                  // 🥺 💀
    public static readonly Emoji[] AssistTargetAlly = { Emoji.ThumbsDown };            // 🕊️ 👎
    public static readonly Emoji[] AggressiveAttack = { Emoji.Laughing };              // 😈
    public static readonly Emoji[] Attack = { Emoji.Angry };                           // 😡
    public static readonly Emoji[] WarshipRetaliation = { Emoji.Anchor };              // ⛵
    public static readonly Emoji[] Nuke = { Emoji.Laughing, Emoji.Angry };             // ☢️ 💥
    public static readonly Emoji[] GotInsulted =
        { Emoji.MiddleFinger, Emoji.Angry, Emoji.Clown, Emoji.Crying };                // 🖕 😡 🤡 😞 😭
    public static readonly Emoji[] Love = { Emoji.Heart, Emoji.Grin };                 // ❤️ 😊 🥰
    public static readonly Emoji[] Confused = { Emoji.Question, Emoji.Clown };         // ❓ 🤡
    public static readonly Emoji[] Brag = { Emoji.Grin, Emoji.Laughing };              // 👑 🥇 💪
    public static readonly Emoji[] CharmAllies = { Emoji.Handshake, Emoji.Grin };      // 🤝 😇 💪
    public static readonly Emoji[] MockTraitor = { Emoji.Clown };                      // 🤡 🤦
    public static readonly Emoji[] Overwhelmed = { Emoji.Sos, Emoji.Crying };          // 💀 🆘 😱 🥺 😭 😞 🫡 👋
    public static readonly Emoji[] ScaredOfThreat = { Emoji.Crying };                  // 🙏 🥺
    public static readonly Emoji[] Bored = { Emoji.Laughing };                         // 🥱
    public static readonly Emoji[] Handshake = { Emoji.Handshake };                    // 🤝
    public static readonly Emoji[] Greet = { Emoji.Grin };                             // 👋

    /// <summary>OpenFront's 🐀 at tiny players: we have no rat emoji, but the quick chat has "Rat.".</summary>
    public const int RatPhrase = 15;
}

/// <summary>
/// When a nation sends emojis, following OpenFront's NationEmojiBehavior: casual ones once
/// per attack cycle (bragging when it's the biggest, charming human allies, mocking
/// traitors, greeting neighbours early on, calling for help when overwhelmed, scoffing at
/// tiny attacks, calling out tiny players), plus the ones its other decisions trigger
/// (attacking, bombing, alliances). Nations only emote at people (or everyone), and at
/// most once per 30 s per person for the "maybe" ones. Victory emojis wait for the win
/// condition (roadmap).
/// </summary>
internal sealed class NationEmojis
{
    private const int MinTicksBetween = 300; // OpenFront: 30 s per person for "maybe" emojis

    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;
    private readonly Dictionary<ushort, int> _lastSent = new();

    public NationEmojis(Game game, Player player, Rng rng)
    {
        _game = game;
        _player = player;
        _rng = rng;
    }

    private bool Chance(int n) => _rng.RangeInt(0, n - 1) == 0;

    public void MaybeSendCasualEmoji()
    {
        CheckOverwhelmedByAttacks();
        CheckVerySmallAttack();
        Brag();
        CharmAllies();
        AnnoyTraitors();
        FindRat();
        GreetNearbyPlayers();
    }

    private void CheckOverwhelmedByAttacks()
    {
        if (!Chance(16))
            return;
        long incoming = 0;
        bool any = false;
        foreach (var a in _game.Attacks)
            if (a.TargetId == _player.Id)
            {
                incoming += a.Troops;
                any = true;
            }
        if (any && incoming >= _player.Troops * 3)
            Send(Game.Nobody, NationMoods.Overwhelmed);
    }

    private void CheckVerySmallAttack()
    {
        if (!Chance(8) || _player.Troops <= 0)
            return;
        foreach (var a in _game.Attacks)
        {
            if (a.TargetId != _player.Id || a.Attacker.Kind != PlayerKind.Human)
                continue;
            if (a.Troops * 10 < _player.Troops)
                MaybeSend(a.Attacker, Chance(2) ? NationMoods.Confused : NationMoods.Bored);
        }
    }

    /// <summary>Brag when we own the most land.</summary>
    private void Brag()
    {
        if (!Chance(300))
            return;
        Player? biggest = null;
        foreach (var p in _game.Players)
            if (biggest is null || p.TileCount > biggest.TileCount)
                biggest = p;
        if (biggest == _player)
            Send(Game.Nobody, NationMoods.Brag);
    }

    private void CharmAllies()
    {
        if (!Chance(250))
            return;
        var humans = new List<Player>();
        foreach (var p in _game.Players)
            if (p.Kind == PlayerKind.Human && _player.IsAlliedWith(p))
                humans.Add(p);
        if (humans.Count == 0)
            return;
        var ally = humans[_rng.RangeInt(0, humans.Count - 1)];
        Send(ally.Id, Chance(3) ? NationMoods.Love : NationMoods.CharmAllies);
    }

    /// <summary>Mock human traitors; only the nation that hates them most does, so the map doesn't pile on.</summary>
    private void AnnoyTraitors()
    {
        if (!Chance(3))
            return;
        foreach (var traitor in _game.Players)
        {
            if (traitor.Kind != PlayerKind.Human || !_game.IsTraitor(traitor))
                continue;
            Player? mocker = null;
            foreach (var p in _game.Players)
            {
                if (!p.IsNation || p.IsAlliedWith(traitor))
                    continue;
                if (mocker is null || _game.RelationValue(p, traitor) < _game.RelationValue(mocker, traitor))
                    mocker = p;
            }
            if (mocker == _player)
                Send(traitor.Id, NationMoods.MockTraitor);
        }
    }

    /// <summary>After 10 minutes, now and then call a tiny human player (under 1% of the land) a rat.</summary>
    private void FindRat()
    {
        if (_game.Tick < 6000 || !Chance(10000))
            return;
        long threshold = _game.Map.LandTileCount / 100;
        var small = new List<Player>();
        foreach (var p in _game.Players)
            if (p.Kind == PlayerKind.Human && p.TileCount > 0 && p.TileCount < threshold)
                small.Add(p);
        if (small.Count == 0)
            return;
        var rat = small[_rng.RangeInt(0, small.Count - 1)];
        if (_game.CanQuickChat(_player, rat))
            _game.Submit(new QuickChatIntent(_player.Id, rat.Id, NationMoods.RatPhrase));
    }

    /// <summary>In the first minute, now and then wave at a neighbouring person.</summary>
    private void GreetNearbyPlayers()
    {
        if (_game.Tick - _game.SpawnPhaseTicks > 600 || !Chance(250))
            return;
        var humans = new List<Player>();
        foreach (var p in AiTiles.Nearby(_game, _player).Players)
            if (p.Kind == PlayerKind.Human)
                humans.Add(p);
        if (humans.Count == 0)
            return;
        Send(humans[_rng.RangeInt(0, humans.Count - 1)].Id, NationMoods.Greet);
    }

    /// <summary>An emoji to this player, at most one per 30 s per person.</summary>
    public void MaybeSend(Player to, Emoji[] mood)
    {
        if (ShouldSend(to.Id, limitByTime: true))
            Send(to.Id, mood);
    }

    /// <summary>Attacking someone: gleeful if we got on with them (they didn't see it coming), angry if we're hitting back.</summary>
    public void MaybeSendAttackEmoji(Player target)
    {
        if (!ShouldSend(target.Id, limitByTime: true))
            return;
        if (_game.RelationOf(_player, target) >= Relation.Neutral)
        {
            if (Chance(2))
                Send(target.Id, NationMoods.AggressiveAttack);
            return;
        }
        if (Chance(4))
            Send(target.Id, NationMoods.Attack);
    }

    /// <summary>An emoji to this player (0 = everyone), if they're a person and the emoji limit allows.</summary>
    public void Send(ushort to, Emoji[] mood)
    {
        if (!ShouldSend(to, limitByTime: false) || !_game.CanSendEmoji(_player, to))
            return;
        _game.Submit(new EmojiIntent(_player.Id, to, mood[_rng.RangeInt(0, mood.Length - 1)]));
    }

    private bool ShouldSend(ushort to, bool limitByTime)
    {
        if (to == Game.Nobody)
            return true;
        if (_game.GetPlayer(to) is not { Kind: PlayerKind.Human })
            return false;
        if (limitByTime)
        {
            int last = _lastSent.GetValueOrDefault(to, -MinTicksBetween);
            if (_game.Tick - last <= MinTicksBetween)
                return false;
            _lastSent[to] = _game.Tick;
        }
        return true;
    }
}
