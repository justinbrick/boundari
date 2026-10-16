using System;
using Godot;

namespace Boundari.View;

/// <summary>
/// The attack ratio slider (10..100%): a row of 91 ticks, one per whole percent, evenly
/// spaced. The ticks swell into a smooth bump around the handle (a vertical line above
/// the tallest tick, with the percentage to its left), and the bump glides when the value
/// changes. Click or drag anywhere on it to pick a tick; the mouse wheel steps one percent.
/// </summary>
public partial class AttackRatioSlider : Control
{
    public const int Min = 10, Max = 100;
    private const int Ticks = Max - Min + 1;

    private const float MinSidePadding = 6;
    private const int TickWidth = 2;
    private const float TickHeight = 8;      // far from the handle
    private const float PeakHeight = 22;     // the tick under the handle
    private const float BumpSpread = 3;      // ticks: about 3x this out, they're back to normal
    private const float HandleGap = 3;       // between the handle line and the tallest tick
    private const float HandleTop = 2;
    private const float BottomMargin = 1;
    private const float GlideSeconds = 0.05f; // eases most of the way in ~0.15 s
    private const int FontSize = 14;
    private static readonly Color TickColour = new(1, 1, 1, 0.4f);
    private static readonly Color PeakColour = new(1, 1, 1, 0.95f);
    private static readonly Color HandleColour = new(1, 1, 1, 0.95f);

    private int _value = 20;
    private float _shown = 20; // where the bump and handle are drawn, gliding towards _value
    private bool _dragging;

    /// <summary>The share of troops to send, in percent (10..100).</summary>
    public int Value
    {
        get => _value;
        set => _value = Math.Clamp(value, Min, Max);
    }

    public AttackRatioSlider()
    {
        MouseFilter = MouseFilterEnum.Stop;
    }

    // Whole pixels between ticks, the same everywhere, with the row centred.
    private int Spacing => Math.Max(TickWidth + 1, (int)((Size.X - 2 * MinSidePadding - TickWidth) / (Ticks - 1)));
    private float RowLeft => MathF.Round((Size.X - (Ticks - 1) * Spacing - TickWidth) / 2);

    /// <summary>Left edge of a tick (fractional percents for the gliding handle).</summary>
    private float TickX(float percent) => RowLeft + (percent - Min) * Spacing;

    private int PercentAt(float x) =>
        Math.Clamp((int)MathF.Round((x - RowLeft - TickWidth / 2f) / Spacing) + Min, Min, Max);

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                _dragging = b.Pressed;
                if (b.Pressed)
                    Value = PercentAt(b.Position.X);
                AcceptEvent();
                break;
            case InputEventMouseMotion m when _dragging:
                Value = PercentAt(m.Position.X);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Value++;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Value--;
                AcceptEvent();
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_shown == _value)
            return;
        _shown += (_value - _shown) * (1 - MathF.Exp(-(float)delta / GlideSeconds));
        if (MathF.Abs(_shown - _value) < 0.01f)
            _shown = _value;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float bottom = MathF.Round(Size.Y - BottomMargin);
        for (int i = 0; i < Ticks; i++)
        {
            // A bell-shaped bump centred on the (gliding) handle.
            float d = (Min + i - _shown) / BumpSpread;
            float lift = MathF.Exp(-d * d / 2);
            float height = TickHeight + (PeakHeight - TickHeight) * lift;
            var colour = TickColour.Lerp(PeakColour, lift);
            float x = TickX(Min + i);
            // Whole pixels, plus a faint top row for the fraction, so it rises smoothly but stays crisp.
            int whole = (int)height;
            DrawRect(new Rect2(x, bottom - whole, TickWidth, whole), colour);
            float fraction = height - whole;
            if (fraction > 0.02f)
                DrawRect(new Rect2(x, bottom - whole - 1, TickWidth, 1), new Color(colour, colour.A * fraction));
        }

        float hx = MathF.Round(TickX(_shown));
        float handleBottom = bottom - PeakHeight - HandleGap;
        DrawRect(new Rect2(hx, HandleTop, TickWidth, handleBottom - HandleTop), HandleColour);

        var font = GetThemeDefaultFont();
        string text = _value + "%";
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize);
        float middle = (HandleTop + handleBottom) / 2;
        var at = new Vector2(MathF.Round(hx - 5 - size.X), MathF.Round(middle + FontSize * 0.36f));
        DrawString(font, at + new Vector2(1, 1), text, HorizontalAlignment.Left, -1, FontSize, new Color(0, 0, 0, 0.35f));
        DrawString(font, at, text, HorizontalAlignment.Left, -1, FontSize, HandleColour);
    }
}
