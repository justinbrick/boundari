using System;
using System.Collections.Generic;
using System.Globalization;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Your side of the bar along the bottom (HBox_Player in boundari.tscn): the troop meter
/// and the structure counts. Nodes are looked up by path and skipped if missing.
/// </summary>
public sealed class PlayerBar
{
    private const string Player = "Main_Layer/Main_Control/Large_Rect/HBoxContainer2/HBox_Player/";

    // Building counts loop over every building on the map, so they refresh 10 times a second.
    private const double CountRefreshSeconds = 0.1;
    private double _sinceCounts = CountRefreshSeconds;

    private readonly Label? _troops, _maxTroops, _cash;

    // OpenFront's gold-gain pip: "+N" pops in above your gold (0.25 s) and stays 2 s; a new
    // gain replaces the old one.
    private const double GainShownSeconds = 2, GainPopSeconds = 0.25;
    private static readonly Color GainColour = new("#4ade80");
    private readonly Label? _gain;
    private double _gainAge = GainShownSeconds;
    private readonly TroopMeterFill? _fill;
    private readonly List<Cell> _cells = new();

    /// <summary>A structure count: its icon and number, dimmed (as built in the scene) while you have none.</summary>
    private sealed class Cell
    {
        public BuildingType Type;
        public Label Label = null!;
        public CanvasItem? Icon;
        public Color DimLabel, DimIcon;
        public bool? Lit;
    }

    // Which HBox_* holds which structure's count.
    private static readonly Dictionary<string, BuildingType> CellTypes = new()
    {
        ["HBox_Harbour"] = BuildingType.Port,
        ["HBox_Barracks"] = BuildingType.City,
        ["HBox_Depot"] = BuildingType.Factory,
        ["HBox_Defense"] = BuildingType.Defense,
        ["HBox_Silo"] = BuildingType.Silo,
        ["HBox_Sam"] = BuildingType.Sam,
        ["HBox_Warship"] = BuildingType.Warship,
    };

    public PlayerBar(Node root)
    {
        _troops = root.GetNodeOrNull<Label>(Player + "TroopMeter/HBoxContainer/Current_Troops");
        _maxTroops = root.GetNodeOrNull<Label>(Player + "TroopMeter/HBoxContainer/Max_Troops");
        _cash = root.GetNodeOrNull<Label>(Player + "Panel_Cash/Label_Cash");
        if (root.GetNodeOrNull<Control>(Player + "Panel_Cash") is { } cashPanel)
        {
            _gain = new Label
            {
                Name = "Gold_Gain",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = false,
            };
            _gain.AddThemeColorOverride("font_color", GainColour);
            _gain.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
            _gain.AddThemeConstantOverride("outline_size", 5);
            _gain.AddThemeFontSizeOverride("font_size", 17);
            if (_cash?.GetThemeFont("font") is { } font)
                _gain.AddThemeFontOverride("font", font);
            cashPanel.AddChild(_gain);
            _gain.SetAnchorsPreset(Control.LayoutPreset.TopWide);
            _gain.OffsetTop = -28;
            _gain.OffsetBottom = -4;
        }
        if (root.GetNodeOrNull<Control>(Player + "TroopMeter") is { } meter)
        {
            // The coloured fill goes behind the numbers, inside the panel's border.
            _fill = new TroopMeterFill { Name = "Fill", MouseFilter = Control.MouseFilterEnum.Ignore };
            _fill.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            meter.AddChild(_fill);
            meter.MoveChild(_fill, 0);
            if (meter is Panel panel && panel.GetThemeStylebox("panel") is StyleBoxFlat box)
                _fill.MatchPanel(box);
        }

        if (root.GetNodeOrNull(Player + "Structures/HBoxContainer2") is { } row)
            foreach (var child in row.GetChildren())
                if (CellTypes.TryGetValue(child.Name, out var type) && child.GetNodeOrNull<Label>("Label") is { } label)
                {
                    var icon = child.GetNodeOrNull<CanvasItem>("TextureRect");
                    _cells.Add(new Cell
                    {
                        Type = type,
                        Label = label,
                        Icon = icon,
                        DimLabel = label.Modulate,
                        DimIcon = icon?.Modulate ?? Colors.White,
                    });
                }
    }

    /// <summary>Shows "+gold" above your gold (the latest gain replaces any showing).</summary>
    public void ShowGoldGain(long gold)
    {
        if (_gain is null || gold <= 0)
            return;
        _gain.Text = "+" + GameHud.FormatCount(gold);
        _gainAge = 0;
        _gain.Visible = true;
        AnimateGain(0);
    }

    private void AnimateGain(double delta)
    {
        if (_gain is null || !_gain.Visible)
            return;
        _gainAge += delta;
        if (_gainAge >= GainShownSeconds)
        {
            _gain.Visible = false;
            return;
        }
        float pop = (float)Math.Min(1, _gainAge / GainPopSeconds);
        float ease = 1 - (1 - pop) * (1 - pop);
        _gain.Modulate = new Color(1, 1, 1, ease);
        _gain.Position = new Vector2(_gain.Position.X, -28 + 4 * (1 - ease)); // rises 4 px into place
    }

    public void Update(Game game, Player me, double delta)
    {
        // Troop meter, like OpenFront's: troops at home out of the cap, then the troops out
        // attacking stacked after them in a lighter blue.
        long attacking = 0;
        foreach (var a in game.Attacks)
            if (a.Attacker == me)
                attacking += a.Troops;
        GameHud.SetText(_cash, me.InfiniteGold ? "$∞" : "$" + GameHud.FormatCount(me.Gold));
        AnimateGain(delta);
        GameHud.SetText(_troops, GameHud.FormatCount(me.Troops));
        GameHud.SetText(_maxTroops, GameHud.FormatCount(me.MaxTroops));
        if (_fill is not null)
        {
            double cap = Math.Max(me.MaxTroops, 1);
            float home = (float)Math.Clamp(me.Troops / cap, 0, 1);
            float away = (float)Math.Clamp(attacking / cap, 0, 1 - home);
            _fill.SetTarget(home, away);
        }

        if ((_sinceCounts += delta) < CountRefreshSeconds)
            return;
        _sinceCounts = 0;
        foreach (var cell in _cells)
        {
            int n = cell.Type == BuildingType.Warship ? game.CountWarships(me) : game.CountLevels(me, cell.Type);
            GameHud.SetText(cell.Label, n.ToString(CultureInfo.InvariantCulture));
            bool lit = n > 0;
            if (cell.Lit == lit)
                continue;
            cell.Lit = lit;
            cell.Label.Modulate = lit ? Colors.White : cell.DimLabel;
            if (cell.Icon is not null)
                cell.Icon.Modulate = lit ? Colors.White : cell.DimIcon;
        }
    }
}

/// <summary>
/// The troop meter's fill: two bars from the left (OpenFront's colours), easing to new
/// lengths over about 0.2 s like OpenFront's, with the panel's rounded corners.
/// </summary>
public partial class TroopMeterFill : Control
{
    // OpenFront's "malibu blue" (troops at home) and "aquarius" (troops out attacking).
    private static readonly Color HomeColour = new("#0084d1");
    private static readonly Color AwayColour = new("#3fa9f5");

    // Eases most of the way to a new length in OpenFront's 0.2 s.
    private const float EaseSeconds = 0.06f;

    private float _home, _away, _homeShown, _awayShown;
    private bool _started;
    private int _inset = 1, _radius = 3;
    private readonly StyleBoxFlat _homeBox = new(), _awayBox = new();

    /// <summary>Sits just inside the panel's border, with its corners rounded to fit.</summary>
    public void MatchPanel(StyleBoxFlat panel)
    {
        _inset = Math.Max(panel.BorderWidthLeft, 0);
        _radius = Math.Max(0, panel.CornerRadiusTopLeft - _inset);
    }

    /// <param name="snap">Jump straight there instead of easing (e.g. it now shows someone else).</param>
    public void SetTarget(float home, float away, bool snap = false)
    {
        _home = home;
        _away = away;
        if (snap)
            _started = false;
        if (!_started)
        {
            _started = true;
            _homeShown = home;
            _awayShown = away;
            QueueRedraw();
        }
    }

    public override void _Process(double delta)
    {
        float k = 1 - MathF.Exp(-(float)delta / EaseSeconds);
        float home = _homeShown + (_home - _homeShown) * k;
        float away = _awayShown + (_away - _awayShown) * k;
        if (MathF.Abs(home - _home) < 0.0005f) home = _home;
        if (MathF.Abs(away - _away) < 0.0005f) away = _away;
        if (home == _homeShown && away == _awayShown)
            return;
        _homeShown = home;
        _awayShown = away;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var inner = new Rect2(Vector2.One * _inset, Size - Vector2.One * (2 * _inset));
        float homeWidth = inner.Size.X * _homeShown;
        float awayWidth = inner.Size.X * _awayShown;
        // Each bar is rounded only where it meets the panel's edge.
        if (homeWidth >= 0.5f)
            DrawBar(_homeBox, HomeColour, new Rect2(inner.Position, new Vector2(homeWidth, inner.Size.Y)),
                left: true, right: homeWidth + awayWidth >= inner.Size.X - 0.5f && awayWidth < 0.5f);
        if (awayWidth >= 0.5f)
            DrawBar(_awayBox, AwayColour, new Rect2(inner.Position + new Vector2(homeWidth, 0), new Vector2(awayWidth, inner.Size.Y)),
                left: homeWidth < 0.5f, right: homeWidth + awayWidth >= inner.Size.X - 0.5f);
    }

    private void DrawBar(StyleBoxFlat box, Color colour, Rect2 rect, bool left, bool right)
    {
        box.BgColor = colour;
        box.AntiAliasing = true;
        box.CornerRadiusTopLeft = box.CornerRadiusBottomLeft = left ? _radius : 0;
        box.CornerRadiusTopRight = box.CornerRadiusBottomRight = right ? _radius : 0;
        DrawStyleBox(box, rect);
    }
}
