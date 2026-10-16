using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The radial menu's chat fans: each of the four arrow wedges opens five buttons beyond the
/// menu in its direction, one feeling each (up happy, right love, down sad, left hate): the
/// feeling's emoji in the middle and two quick-chat phrases either side. Placeholder look.
/// </summary>
public sealed class RadialChat
{
    /// <summary>Arrow wedge → (feeling, which way the fan opens).</summary>
    public static readonly Dictionary<string, (Emoji Feeling, Vector2 Direction)> Arrows = new()
    {
        ["T_Rect"] = (Emoji.Grin, Vector2.Up),
        ["R_Rect"] = (Emoji.Heart, Vector2.Right),
        ["B_Rect"] = (Emoji.Crying, Vector2.Down),
        ["L_Rect"] = (Emoji.MiddleFinger, Vector2.Left),
    };

    private const float Reach = 170;          // from the menu's centre to the fan's near edge
    private const float Gap = 8;
    private const float EmojiSize = 54;
    private const float PhraseHeight = 34;
    private const int PhraseFontSize = 15;
    private static readonly float[] PullIn = { 0, 8, 28 }; // outer buttons sit nearer the menu, so the fan curves

    private static Font? _emojiFont;

    /// <summary>A font that draws colour emoji (the system's emoji font).</summary>
    public static Font EmojiFont => _emojiFont ??= new SystemFont
    {
        FontNames = new[] { "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji" },
    };

    private readonly Control _menu;
    private readonly Vector2 _centre;
    private Control? _fan;
    private string? _open;

    /// <param name="menu">The radial menu's root (the fan is its child, so it closes with it).</param>
    public RadialChat(Control menu)
    {
        _menu = menu;
        _centre = menu.Size == Vector2.Zero ? new Vector2(190, 190) : menu.Size / 2;
    }

    /// <summary>
    /// Opens (or, if it's already open, closes) the fan for this arrow wedge. The emoji
    /// button and phrase buttons call back with which was picked; disabled ones can't be.
    /// </summary>
    public void Toggle(string arrow, bool canEmoji, bool canChat, Action<Emoji> sendEmoji, Action<int> sendPhrase)
    {
        _fan?.QueueFree();
        _fan = null;
        if (_open == arrow || !Arrows.TryGetValue(arrow, out var fan))
        {
            _open = null;
            return;
        }
        _open = arrow;
        _fan = new Control { Name = "ChatFan", MouseFilter = Control.MouseFilterEnum.Ignore };
        _menu.AddChild(_fan);

        // Order along the fan: phrase, phrase, emoji, phrase, phrase.
        int first = (int)fan.Feeling * 4;
        var buttons = new Button[5];
        for (int k = 0; k < 5; k++)
        {
            if (k == 2)
            {
                buttons[k] = MakeButton(ChatRules.Glyph(fan.Feeling), EmojiFont, 30, canEmoji);
                buttons[k].CustomMinimumSize = new Vector2(EmojiSize, EmojiSize);
                buttons[k].Pressed += () => sendEmoji(fan.Feeling);
            }
            else
            {
                int phrase = first + (k < 2 ? k : k - 1);
                buttons[k] = MakeButton(ChatRules.Phrases[phrase], null, PhraseFontSize, canChat);
                buttons[k].CustomMinimumSize = new Vector2(0, PhraseHeight);
                buttons[k].Pressed += () => sendPhrase(phrase);
            }
            _fan.AddChild(buttons[k]);
            buttons[k].Size = buttons[k].GetCombinedMinimumSize();
        }
        Layout(buttons, fan.Direction);
    }

    /// <summary>Lines the buttons up across the direction, just beyond the menu, curving back at the ends.</summary>
    private void Layout(Button[] buttons, Vector2 direction)
    {
        bool row = direction.Y != 0; // up/down: a row; left/right: a column
        float Along(Button b) => row ? b.Size.X : b.Size.Y;
        float Across(Button b) => row ? b.Size.Y : b.Size.X;

        // Where each button's middle sits along the line (the emoji at 0).
        var middles = new float[5];
        for (int side = -1; side <= 1; side += 2)
        {
            float edge = Along(buttons[2]) / 2;
            for (int step = 1; step <= 2; step++)
            {
                var b = buttons[2 + side * step];
                middles[2 + side * step] = side * (edge + Gap + Along(b) / 2);
                edge += Gap + Along(b);
            }
        }
        for (int k = 0; k < 5; k++)
        {
            var b = buttons[k];
            float near = Reach - PullIn[Math.Abs(k - 2)];       // distance of the button's near edge from the centre
            float acrossMiddle = near + Across(b) / 2;
            var middle = row
                ? new Vector2(middles[k], direction.Y * acrossMiddle)
                : new Vector2(direction.X * acrossMiddle, middles[k]);
            b.Position = _centre + middle - b.Size / 2;
        }
    }

    private static Button MakeButton(string text, Font? font, int fontSize, bool enabled)
    {
        var button = new Button
        {
            Text = text,
            Disabled = !enabled,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        if (font is not null)
            button.AddThemeFontOverride("font", font);
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", Colors.White);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_disabled_color", new Color(1, 1, 1, 0.35f));
        button.AddThemeStyleboxOverride("normal", Box(new Color(0.08f, 0.09f, 0.1f, 0.82f)));
        button.AddThemeStyleboxOverride("hover", Box(new Color(0.22f, 0.24f, 0.27f, 0.9f)));
        button.AddThemeStyleboxOverride("pressed", Box(new Color(0.03f, 0.03f, 0.04f, 0.9f)));
        button.AddThemeStyleboxOverride("disabled", Box(new Color(0.08f, 0.09f, 0.1f, 0.4f)));
        return button;
    }

    private static StyleBoxFlat Box(Color colour)
    {
        var box = new StyleBoxFlat
        {
            BgColor = colour,
            BorderColor = new Color(1, 1, 1, 0.14f),
            ContentMarginLeft = 14,
            ContentMarginRight = 14,
            AntiAliasing = true,
        };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(17);
        return box;
    }
}
