using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The emoji pictures: Microsoft's Fluent Emoji, Color style (MIT, ui/emoji/LICENSE.txt).
/// ui/emoji/NAME.svg is the original; NAME.png is it drawn at 512 px by a browser, because
/// Godot's SVG importer gets the inner shadows wrong and blurs the whole picture.
/// </summary>
public static class EmojiIcons
{
    private static readonly Dictionary<Emoji, Texture2D?> Loaded = new();

    /// <summary>The emoji's picture, or null if its file is missing.</summary>
    public static Texture2D? Texture(Emoji emoji)
    {
        if (!Loaded.TryGetValue(emoji, out var texture))
        {
            string png = $"res://ui/emoji/{File(emoji)}.png", svg = $"res://ui/emoji/{File(emoji)}.svg";
            string? path = ResourceLoader.Exists(png) ? png : ResourceLoader.Exists(svg) ? svg : null;
            Loaded[emoji] = texture = path is null ? null : GD.Load<Texture2D>(path);
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
