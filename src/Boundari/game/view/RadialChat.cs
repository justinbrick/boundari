using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The radial menu's chat fans: each of the four arrow wedges opens five buttons beyond the
/// menu in its direction, one feeling each (up happy, right love, down sad, left hate): the
/// feeling's emoji in the middle and two quick-chat phrases either side. The buttons are
/// wedges of a ring around the menu, frosted glass like the menu's own wedges.
/// </summary>
public sealed partial class RadialChat
{
    /// <summary>Arrow wedge → (feeling, which way the fan opens).</summary>
    public static readonly Dictionary<string, (Emoji Feeling, Vector2 Direction)> Arrows = new()
    {
        ["T_Rect"] = (Emoji.Grin, Vector2.Up),
        ["R_Rect"] = (Emoji.Heart, Vector2.Right),
        ["B_Rect"] = (Emoji.Crying, Vector2.Down),
        ["L_Rect"] = (Emoji.MiddleFinger, Vector2.Left),
    };

    // The ring, in screen pixels from the menu's centre (the menu itself reaches about 152).
    private const float InnerRadius = 160;
    private const float OuterRadius = 248;
    private const float EmojiDegrees = 24;    // how much of the ring each wedge takes
    private const float PhraseDegrees = 30;

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
    /// wedge and phrase wedges call back with which was picked; disabled ones can't be.
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

        // Order along the fan (left to right, or top to bottom): phrase, phrase, emoji, phrase, phrase.
        float facing = Mathf.RadToDeg(fan.Direction.Angle());
        float sign = fan.Direction == Vector2.Up || fan.Direction == Vector2.Right ? 1 : -1;
        float from = -(EmojiDegrees + 4 * PhraseDegrees) / 2;
        int first = (int)fan.Feeling * 4;
        var wedges = new List<ChatWedge>();
        for (int k = 0; k < 5; k++)
        {
            float span = k == 2 ? EmojiDegrees : PhraseDegrees;
            float a0 = facing + sign * from, a1 = facing + sign * (from + span);
            from += span;
            ChatWedge wedge;
            if (k == 2)
            {
                wedge = new ChatWedge(_centre, a0, a1, null, EmojiIcons.Texture(fan.Feeling), canEmoji);
                wedge.Clicked += () => sendEmoji(fan.Feeling);
            }
            else
            {
                int phrase = first + (k < 2 ? k : k - 1);
                wedge = new ChatWedge(_centre, a0, a1, ChatRules.Phrases[phrase], null, canChat);
                wedge.Clicked += () => sendPhrase(phrase);
            }
            _fan.AddChild(wedge);
            wedges.Add(wedge);
        }
        // Every phrase in the fan at one size: the biggest that fits them all.
        var font = _menu.GetThemeDefaultFont();
        int fontSize = int.MaxValue;
        foreach (var w in wedges)
            fontSize = Math.Min(fontSize, w.BiggestFontSize(font));
        foreach (var w in wedges)
            w.SetFont(font, fontSize);
    }

    /// <summary>
    /// One button of the fan: a wedge of the ring between two angles, with rounded corners and
    /// even gaps to its neighbours. It's a frosted-glass panel (blur.gdshader, like the menu's
    /// wedges) that lightens on hover and darkens while pressed; a face on top draws its rim
    /// and its phrase or emoji.
    /// </summary>
    private sealed partial class ChatWedge : Control
    {
        private const float Gap = 7;            // between neighbouring wedges, and to the menu
        private const float CornerRadius = 9;
        private const float EmojiPixels = 46;
        private const int MaxFontSize = 16;
        private const int MinFontSize = 11;
        private const float HoverTint = 1.45f;  // the menu's own wedges' (radial_menu.gd)
        private const float PressedTint = 0.6f;
        private const float DisabledTint = 0.75f;
        private static readonly Shader Glass = GD.Load<Shader>("res://blur.gdshader");

        public event Action? Clicked;

        private readonly Vector2[] _shape;  // in this control's own coordinates
        private readonly Vector2 _middle;   // where the label or emoji goes
        private readonly string? _text;
        private readonly Texture2D? _icon;
        private readonly bool _enabled;
        private readonly ShaderMaterial _glass;
        private readonly Face _face;
        private bool _hovered, _pressed;
        private Font? _font;
        private int _fontSize = MinFontSize;
        private string[] _lines = Array.Empty<string>();

        /// <param name="centre">The menu's centre (in the fan's coordinates).</param>
        /// <param name="a0">One side of the wedge, in degrees (0 = right, 90 = down).</param>
        /// <param name="a1">The other side.</param>
        public ChatWedge(Vector2 centre, float a0, float a1, string? text, Texture2D? icon, bool enabled)
        {
            _text = text;
            _icon = icon;
            _enabled = enabled;
            MouseFilter = MouseFilterEnum.Stop; // (a disabled one still swallows the click)
            FocusMode = FocusModeEnum.None;

            // The whole wedge from boundary to boundary, then shrunk by half the gap (so the
            // gaps keep one width all the way out) and rounded: shrinking further and growing
            // back with round joins rounds every outward corner.
            var full = new List<Vector2>();
            float lo = Mathf.DegToRad(Math.Min(a0, a1)), hi = Mathf.DegToRad(Math.Max(a0, a1));
            int steps = Math.Max(8, (int)((hi - lo) * OuterRadius / 6));
            float outer = OuterRadius + Gap / 2, inner = InnerRadius - Gap / 2;
            for (int i = 0; i <= steps; i++)
                full.Add(Vector2.FromAngle(lo + (hi - lo) * i / steps) * outer);
            for (int i = steps; i >= 0; i--)
                full.Add(Vector2.FromAngle(lo + (hi - lo) * i / steps) * inner);
            var shape = Rounded(full.ToArray());

            var min = shape[0];
            var max = shape[0];
            foreach (var p in shape)
            {
                min = min.Min(p);
                max = max.Max(p);
            }
            Position = centre + min;
            Size = max - min;
            _shape = new Vector2[shape.Length];
            for (int i = 0; i < shape.Length; i++)
                _shape[i] = shape[i] - min;
            _middle = Vector2.FromAngle((lo + hi) / 2) * ((InnerRadius + OuterRadius) / 2) - min;

            _glass = new ShaderMaterial { Shader = Glass };
            _glass.SetShaderParameter("blur_amount", 3.8f);
            _glass.SetShaderParameter("darken_amount", 0.62f);
            _glass.SetShaderParameter("noise_strength", 0.016f);
            _glass.SetShaderParameter("grain_scale", 150f);
            _glass.SetShaderParameter("noise_speed", 0f);
            Material = _glass;
            _face = new Face(this) { MouseFilter = MouseFilterEnum.Ignore };
            AddChild(_face);
            MouseEntered += () => { _hovered = _enabled; Refresh(); };
            MouseExited += () => { _hovered = false; _pressed = false; Refresh(); };
            Refresh();
        }

        private static Vector2[] Rounded(Vector2[] polygon)
        {
            var shrunk = Geometry2D.OffsetPolygon(polygon, -(Gap / 2 + CornerRadius), Geometry2D.PolyJoinType.Miter);
            if (shrunk.Count == 0)
                return polygon;
            var grown = Geometry2D.OffsetPolygon(shrunk[0], CornerRadius, Geometry2D.PolyJoinType.Round);
            return grown.Count == 0 ? shrunk[0] : grown[0];
        }

        public override void _Ready() => _face.Size = Size;

        public override bool _HasPoint(Vector2 point) => Geometry2D.IsPointInPolygon(point, _shape);

        public override void _GuiInput(InputEvent e)
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
                return;
            AcceptEvent();
            if (!_enabled)
                return;
            if (button.Pressed)
                _pressed = true;
            else if (_pressed)
            {
                _pressed = false;
                if (_HasPoint(button.Position))
                    Clicked?.Invoke();
            }
            Refresh();
        }

        private void Refresh()
        {
            float tint = !_enabled ? DisabledTint : _pressed ? PressedTint : _hovered ? HoverTint : 1;
            _glass.SetShaderParameter("tint", tint);
            _face.QueueRedraw();
        }

        public override void _Draw() => DrawColoredPolygon(_shape, Colors.White); // the glass (coloured by the shader)

        /// <summary>The wedge's rim and its phrase or emoji (drawn without the glass shader).</summary>
        private sealed partial class Face : Control
        {
            private readonly ChatWedge _wedge;

            public Face(ChatWedge wedge) => _wedge = wedge;

            public override void _Draw()
            {
                var w = _wedge;
                float alpha = w._enabled ? 1 : 0.35f;
                // A soft white wash and rim, like the menu's pale arrow wedges.
                DrawColoredPolygon(w._shape, new Color(1, 1, 1, w._enabled && w._hovered ? 0.1f : 0.05f));
                var rim = new Vector2[w._shape.Length + 1];
                w._shape.CopyTo(rim, 0);
                rim[^1] = w._shape[0];
                DrawPolyline(rim, new Color(1, 1, 1, w._hovered && w._enabled ? 0.55f : 0.3f), 1.5f, true);

                if (w._icon is { } icon)
                {
                    var size = new Vector2(EmojiPixels, EmojiPixels);
                    DrawTextureRect(icon, new Rect2((w._middle - size / 2).Round(), size), false, new Color(1, 1, 1, alpha));
                }
                DrawLabel(alpha);
            }

            /// <summary>The phrase, level and centred on the wedge's middle.</summary>
            private void DrawLabel(float alpha)
            {
                var w = _wedge;
                if (w._font is not { } font)
                    return;
                int fontSize = w._fontSize;
                float lineHeight = MathF.Round(fontSize * 1.15f);
                float top = w._middle.Y - lineHeight * w._lines.Length / 2;
                for (int i = 0; i < w._lines.Length; i++)
                {
                    string line = w._lines[i];
                    float width = font.GetStringSize(line, HorizontalAlignment.Left, -1, fontSize).X;
                    var at = new Vector2(w._middle.X - width / 2, top + lineHeight * i + (lineHeight - font.GetHeight(fontSize)) / 2 + font.GetAscent(fontSize)).Round();
                    DrawStringOutline(font, at, line, HorizontalAlignment.Left, -1, fontSize, 4, new Color(0, 0, 0, 0.55f * alpha));
                    DrawString(font, at, line, HorizontalAlignment.Left, -1, fontSize, new Color(1, 1, 1, alpha));
                }
            }
        }

        /// <summary>The biggest font size at which the phrase fits inside the wedge (on one line or two).</summary>
        public int BiggestFontSize(Font font)
        {
            if (_text is null)
                return MaxFontSize;
            for (int size = MaxFontSize; size > MinFontSize; size--)
                if (Array.Exists(SplitAt(_text), option => Fits(font, option, size)))
                    return size;
            return MinFontSize;
        }

        /// <summary>Lays the phrase out at this size: on one line if it fits, otherwise on two.</summary>
        public void SetFont(Font font, int fontSize)
        {
            _font = font;
            _fontSize = fontSize;
            if (_text is not null)
            {
                var layouts = SplitAt(_text);
                _lines = Array.Find(layouts, option => Fits(font, option, fontSize)) ?? layouts[^1];
            }
            _face.QueueRedraw();
        }

        /// <summary>The phrase on one line, then on two (split at each space, most even first).</summary>
        private static string[][] SplitAt(string text)
        {
            var options = new List<string[]> { new[] { text } };
            var splits = new List<int>();
            for (int i = 0; i < text.Length; i++)
                if (text[i] == ' ')
                    splits.Add(i);
            splits.Sort((a, b) => Math.Abs(text.Length - 2 * a).CompareTo(Math.Abs(text.Length - 2 * b)));
            foreach (int i in splits)
                options.Add(new[] { text[..i], text[(i + 1)..] });
            return options.ToArray();
        }

        /// <summary>Whether these lines' box (plus a little room) lies inside the wedge.</summary>
        private bool Fits(Font font, string[] lines, int fontSize)
        {
            float width = 0;
            foreach (var line in lines)
                width = Math.Max(width, font.GetStringSize(line, HorizontalAlignment.Left, -1, fontSize).X);
            var half = new Vector2(width / 2 + 6, MathF.Round(fontSize * 1.15f) * lines.Length / 2 + 4);
            foreach (var corner in new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) })
                if (!Geometry2D.IsPointInPolygon(_middle + half * corner, _shape))
                    return false;
            return true;
        }
    }
}
