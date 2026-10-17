using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The emoji pictures: Microsoft's Fluent Emoji, Color style (MIT, ui/emoji/LICENSE.txt),
/// one res://ui/emoji/NAME.svg per emoji.
/// </summary>
public static class EmojiIcons
{
    private static readonly Dictionary<Emoji, Texture2D?> Loaded = new();

    /// <summary>The emoji's picture, or null if its file is missing.</summary>
    public static Texture2D? Texture(Emoji emoji)
    {
        if (!Loaded.TryGetValue(emoji, out var texture))
        {
            string path = $"res://ui/emoji/{File(emoji)}.svg";
            Loaded[emoji] = texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        }
        return texture;
    }

    private static string File(Emoji emoji) => emoji switch
    {
        Emoji.Grin => "grin",
        Emoji.Heart => "heart",
        Emoji.Crying => "crying",
        Emoji.MiddleFinger => "middle_finger",
        Emoji.Anchor => "anchor",
        Emoji.City => "city",
        Emoji.Factory => "factory",
        Emoji.Shield => "shield",
        Emoji.Question => "question",
        Emoji.Sos => "sos",
        Emoji.MoneyBag => "money_bag",
        Emoji.ThumbsDown => "thumbs_down",
        Emoji.Handshake => "handshake",
        Emoji.Clown => "clown",
        Emoji.Laughing => "laughing",
        _ => "angry",
    };
}
