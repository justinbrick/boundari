using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>
/// The emojis (the user's sixteen). The first four are the radial menu's feelings, in
/// order (the quick-chat phrases are numbered by them); the rest are for nations to use.
/// </summary>
public enum Emoji : byte
{
    Grin,         // happy
    Heart,        // love
    Crying,       // sad
    MiddleFinger, // hate
    Anchor,
    City,
    Factory,
    Shield,
    Question,
    Sos,
    MoneyBag,
    ThumbsDown,
    Handshake,
    Clown,
    Laughing,
    Angry,
}

/// <summary>Emojis and quick-chat phrases, and OpenFront's limits on sending them.</summary>
public static class ChatRules
{
    public const int EmojiShownTicks = 5 * 10;   // an emoji shows over the sender's name this long
    public const int EmojiWindowTicks = 5 * 10;  // a person may send...
    public const int EmojiLimit = 5;             // ...this many to the same recipient per window (AI: 1)
    public const int QuickChatCooldownTicks = 3 * 10;
    public const int ReplyMinTicks = 1 * 10;     // a nation answers an emoji after 1..3 s (ours: OpenFront answers at once)
    public const int ReplyMaxTicks = 3 * 10;

    /// <summary>The quick-chat phrases, four per feeling (Phrases[(int)feeling * 4 + k]); see the radial menu.</summary>
    public static readonly string[] Phrases =
    {
        "Save your troops!", "Save your money!", "Send me troops!", "Send me money!", // happy
        "Thank you", "I love you...", "No problem", "I love you too.",                // love
        "Why?", "Sorry", "Help!", "SOS",                                              // sad
        "Bad idea.", "Fuck you.", "Be warned.", "Rat.",                               // hate
    };
}

/// <summary>Sends an emoji to a player, or to everyone (<paramref name="RecipientId"/> 0).</summary>
public sealed record EmojiIntent(ushort PlayerId, ushort RecipientId, Emoji Emoji) : Intent(PlayerId);

/// <summary>Sends a quick-chat phrase (an index into <see cref="ChatRules.Phrases"/>) to a player.</summary>
public sealed record QuickChatIntent(ushort PlayerId, ushort RecipientId, int Phrase) : Intent(PlayerId);

// Emojis and quick chat, as in OpenFront: an emoji shows over the sender's name for 5 s to
// whoever it was sent to (or everyone); a phrase goes to one player's messages. Nations
// answer a middle finger in kind (and turn hostile), a clown with puzzlement (and like you
// a little less), and a heart with love if they don't mind you (puzzlement otherwise).
public sealed partial class Game
{
    public bool CanSendEmoji(Player from, ushort to)
    {
        if (to == from.Id || !from.IsAlive)
            return false;
        int recent = 0;
        foreach (var (tick, recipient, _) in from.Emojis)
            if (recipient == to && Tick - tick < ChatRules.EmojiWindowTicks)
                recent++;
        return recent < (from.Kind == PlayerKind.Human ? ChatRules.EmojiLimit : 1);
    }

    public bool CanQuickChat(Player from, Player to) =>
        from != to && from.IsAlive && to.IsAlive
        && (!from.LastQuickChat.TryGetValue(to.Id, out int last) || Tick - last >= ChatRules.QuickChatCooldownTicks);

    /// <summary>The emoji this player is showing to the viewer right now (sent to them or to everyone), if any.</summary>
    public Emoji? ShownEmoji(Player sender, Player? viewer)
    {
        for (int i = sender.Emojis.Count - 1; i >= 0; i--)
        {
            var (tick, to, emoji) = sender.Emojis[i];
            if (Tick - tick >= ChatRules.EmojiShownTicks)
                break;
            if (to == Nobody || (viewer is not null && to == viewer.Id))
                return emoji;
        }
        return null;
    }

    private void SendEmoji(Player from, ushort to, Emoji emoji)
    {
        if ((to != Nobody && GetPlayer(to) is null) || !CanSendEmoji(from, to))
            return;
        from.Emojis.RemoveAll(e => Tick - e.Tick >= ChatRules.EmojiWindowTicks && Tick - e.Tick >= ChatRules.EmojiShownTicks);
        from.Emojis.Add((Tick, to, emoji));

        // OpenFront's nations answer some emojis sent to them. Their feelings change at once;
        // the answer comes a moment later, as if someone read it first.
        if (GetPlayer(to) is not { IsNation: true } nation || !CanSendEmoji(nation, from.Id))
            return;
        if (emoji == Emoji.MiddleFinger)
        {
            UpdateRelation(nation, from, -AllianceRules.RelationMax);
            Reply(nation, from, Pick(NationMoods.GotInsulted));
        }
        else if (emoji == Emoji.Clown)
        {
            UpdateRelation(nation, from, -1_000);
            Reply(nation, from, Pick(NationMoods.Confused));
        }
        else if (emoji == Emoji.Heart)
        {
            if (Difficulty == Difficulty.Easy)
                UpdateRelation(nation, from, 1_500);
            Reply(nation, from, Pick(RelationOf(from, nation) >= Relation.Neutral ? NationMoods.Love : NationMoods.Confused));
        }
    }

    /// <summary>Emoji answers waiting to go out, in the order they were decided.</summary>
    private readonly List<(int Tick, Player From, Player To, Emoji Emoji)> _replies = new();

    private void Reply(Player from, Player to, Emoji emoji)
    {
        if (!_replies.Exists(r => r.From == from && r.To == to)) // one answer at a time to the same player
            _replies.Add((Tick + _rng.RangeInt(ChatRules.ReplyMinTicks, ChatRules.ReplyMaxTicks), from, to, emoji));
    }

    /// <summary>Each tick: sends the answers that are due (unless the sender has since been wiped out).</summary>
    private void SendReplies()
    {
        for (int i = 0; i < _replies.Count; i++)
        {
            var (due, from, to, emoji) = _replies[i];
            if (due > Tick)
                continue;
            _replies.RemoveAt(i--);
            if (from.IsAlive && to.IsAlive)
                SendEmoji(from, to.Id, emoji);
        }
    }

    private Emoji Pick(Emoji[] mood) => mood[_rng.RangeInt(0, mood.Length - 1)];

    private void SendQuickChat(Player from, Player to, int phrase)
    {
        if (phrase < 0 || phrase >= ChatRules.Phrases.Length || !CanQuickChat(from, to))
            return;
        from.LastQuickChat[to.Id] = Tick;
        Report(GameEventKind.QuickChat, from, to, phrase);
    }
}
