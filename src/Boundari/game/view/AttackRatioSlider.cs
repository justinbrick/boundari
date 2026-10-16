using System;
using Godot;

namespace Boundari.View;

/// <summary>
/// The attack ratio slider (10..100%): a row of 91 short ticks, one per whole percent, with a
/// vertical line above them for the handle and the percentage to its left. Click or drag
/// anywhere on it to pick a tick; the mouse wheel steps one percent.
/// </summary>
public partial class AttackRatioSlider : Control
{
    public const int Min = 10, Max = 100;

    private const float SidePadding = 10;
    private const float TickHeight = 6;
    private const float HandleGap = 2; // between the handle's bottom and the ticks
    private const float HandleTop = 3;
    private const int FontSize = 14;
    private static readonly Color TickColour = new(1, 1, 1, 0.35f);
    private static readonly Color HandleColour = new(1, 1, 1, 0.95f);

    private int _value = 20;
    private bool _dragging;

    /// <summary>The share of troops to send, in percent (10..100).</summary>
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Min, Max);
            if (v == _value)
                return;
            _value = v;
            QueueRedraw();
        }
    }

    public AttackRatioSlider()
    {
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>Where a tick sits across the control (whole pixels, so the ticks stay crisp).</summary>
    private float TickX(int percent)
    {
        float step = (Size.X - 2 * SidePadding) / (Max - Min);
        return MathF.Round(SidePadding + (percent - Min) * step);
    }

    private int PercentAt(float x)
    {
        float step = (Size.X - 2 * SidePadding) / (Max - Min);
        return Math.Clamp((int)MathF.Round((x - SidePadding) / step) + Min, Min, Max);
    }

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

    public override void _Draw()
    {
        float bottom = MathF.Round(Size.Y) - 2;
        float tickTop = bottom - TickHeight;
        for (int p = Min; p <= Max; p++)
        {
            float x = TickX(p);
            DrawRect(new Rect2(x, tickTop, 1, TickHeight), TickColour);
        }

        float hx = TickX(_value); // the handle is 2 px wide: the tick's pixel and the next
        float handleBottom = tickTop - HandleGap;
        DrawRect(new Rect2(hx, HandleTop, 2, handleBottom - HandleTop), HandleColour);

        var font = GetThemeDefaultFont();
        string text = _value + "%";
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize);
        float middle = (HandleTop + handleBottom) / 2;
        var at = new Vector2(MathF.Round(hx - 5 - size.X), MathF.Round(middle + FontSize * 0.36f));
        DrawString(font, at + new Vector2(1, 1), text, HorizontalAlignment.Left, -1, FontSize, new Color(0, 0, 0, 0.35f));
        DrawString(font, at, text, HorizontalAlignment.Left, -1, FontSize, HandleColour);
    }
}
