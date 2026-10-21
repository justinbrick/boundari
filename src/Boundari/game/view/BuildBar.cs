using System.Collections.Generic;
using System.Globalization;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The build buttons (ButtonsRow in boundari.tscn): each button's picture lifts a little while
/// hovered and settles back after, and the number on it is how many of that structure you have
/// (levels count, like OpenFront's bar). Buttons without a Label child (the bombs) just lift.
/// While a button is hovered, the attack ratio row above them (AttackRatio_Hbox) gives way to
/// Description_HBox: that structure's icon, name, what it does and its current price.
/// </summary>
public sealed class BuildBar
{
    private const string Row = "Main_Layer/Main_Control/ButtonsRow";
    private const string Panel = "Main_Layer/Main_Control/HBox_AHudTEST/In_Panel2/";
    private const float LiftPixels = 6;
    private const double LiftSeconds = 0.12;

    // Building counts loop over every building on the map, so they refresh 10 times a second.
    private const double CountRefreshSeconds = 0.1;
    private double _sinceCounts = CountRefreshSeconds;

    /// <summary>One button: what it builds (or fires), its name, description and small icon.</summary>
    private sealed record Info(BuildingType? Building, NukeType? Nuke, string Name, string Description, string? Icon);

    private static readonly Dictionary<string, Info> Buttons = new()
    {
        ["Port_Button"] = new(BuildingType.Port, null, "Harbor", "Sends trade ships to other harbors to earn gold", "res://ui/structures32x/harbour32x.svg"),
        ["Barracks_Button"] = new(BuildingType.City, null, "Barracks", "Raises how many troops you can hold", "res://ui/structures32x/barracks32x.svg"),
        ["Fac_Button"] = new(BuildingType.Factory, null, "Depot", "Lays railroads; trains earn gold between your structures", "res://ui/structures32x/depot32x.svg"),
        ["Def_Button"] = new(BuildingType.Defense, null, "Defense", "Makes nearby borders much harder to attack", "res://ui/structures32x/defense32x.svg"),
        ["Silo_Button"] = new(BuildingType.Silo, null, "Silo", "Launches Mortars, Missiles and Atomic Bombs", "res://ui/structures32x/silo32x.svg"),
        ["Sam_Button"] = new(BuildingType.Sam, null, "Flak", "Shoots down enemy Mortars and Missiles flying nearby", "res://ui/structures32x/flak32x.svg"),
        ["Ship_Button"] = new(BuildingType.Warship, null, "Warship", "Patrols the sea, capturing trade ships and sinking boats", "res://ui/structures32x/warship32x.svg"),
        ["Viper_Button"] = new(null, NukeType.Moab, "Mortar", "Small blast that wipes out land and structures", "res://ui/structures32x/mortar32x.svg"),
        ["Falcon_Button"] = new(null, NukeType.FatMan, "Missile", "Large blast over a wide area", "res://ui/structures32x/missile32x.svg"),
        ["Raptor_Button"] = new(null, NukeType.TsarBomba, "Atomic Bomb", "Splits into many warheads on one player's land; Flak can't stop it", "res://ui/structures32x/atomicbomb32x.svg"),
    };

    private readonly List<(BuildingType Type, Label Label)> _counts = new();

    // The description row and the attack ratio row it takes the place of.
    private readonly Control? _ratioRow, _descriptionRow;
    private readonly TextureRect? _icon;
    private readonly Label? _name, _description, _cost;
    private Info? _hovered;

    public BuildBar(Node root)
    {
        _ratioRow = root.GetNodeOrNull<Control>(Panel + "AttackRatio_Hbox");
        _descriptionRow = root.GetNodeOrNull<Control>(Panel + "Description_HBox");
        _icon = _descriptionRow?.FindChild("StructureIcon", true, false) as TextureRect;
        _name = _descriptionRow?.FindChild("StructureName", true, false) as Label;
        _description = _descriptionRow?.FindChild("StructureDescription", true, false) as Label;
        _cost = _descriptionRow?.FindChild("StructureCost", true, false) as Label;
        ShowDescription(null);

        if (root.GetNodeOrNull(Row) is not { } row)
            return;
        foreach (var wrapper in row.GetChildren())
        {
            var button = wrapper as Button ?? FirstButton(wrapper);
            if (button?.GetNodeOrNull<Control>("TextureRect") is not { } picture)
                continue;
            AddLift(button, picture);
            if (!Buttons.TryGetValue(button.Name, out var info))
                continue;
            if (info.Building is { } type && picture.GetNodeOrNull<Label>("Label") is { } label)
                _counts.Add((type, label));
            button.MouseEntered += () => ShowDescription(info);
            button.MouseExited += () =>
            {
                if (_hovered == info)
                    ShowDescription(null);
            };
        }
    }

    private static Button? FirstButton(Node parent)
    {
        foreach (var child in parent.GetChildren())
            if (child is Button button)
                return button;
        return null;
    }

    /// <summary>Hovering slides the picture (and its number) up; leaving slides it back.</summary>
    private static void AddLift(Button button, Control picture)
    {
        // Offsets, not position: they are what the scene sets, so they stay right when the window resizes.
        float top = picture.OffsetTop, bottom = picture.OffsetBottom;
        Tween? tween = null;
        void Slide(float up)
        {
            tween?.Kill();
            tween = picture.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(picture, "offset_top", top - up, LiftSeconds);
            tween.TweenProperty(picture, "offset_bottom", bottom - up, LiftSeconds);
        }
        button.MouseEntered += () => Slide(LiftPixels);
        button.MouseExited += () => Slide(0);
    }

    /// <summary>Swaps the attack ratio row for this button's description, or back (null).</summary>
    private void ShowDescription(Info? info)
    {
        _hovered = info;
        // Without a description row there's nothing to swap to: the ratio row stays.
        if (_descriptionRow is null)
            return;
        _descriptionRow.Visible = info is not null;
        if (_ratioRow is not null)
            _ratioRow.Visible = info is null;
        if (info is null)
            return;
        GameHud.SetText(_name, info.Name);
        GameHud.SetText(_description, "- " + info.Description);
        if (_icon is not null)
        {
            // No small icon for it yet (the bombs): none at all rather than the wrong one.
            bool hasIcon = info.Icon is not null && ResourceLoader.Exists(info.Icon);
            _icon.Visible = hasIcon;
            if (hasIcon)
                _icon.Texture = GD.Load<Texture2D>(info.Icon);
        }
        _sinceCounts = CountRefreshSeconds; // the price, on the next frame
    }

    public void Update(Game game, Player me, double delta)
    {
        if ((_sinceCounts += delta) < CountRefreshSeconds)
            return;
        _sinceCounts = 0;
        foreach (var (type, label) in _counts)
        {
            int n = type == BuildingType.Warship ? game.CountWarships(me) : game.CountLevels(me, type);
            GameHud.SetText(label, n.ToString(CultureInfo.InvariantCulture));
        }
        if (_hovered is { } info)
        {
            long cost = info.Building is { } b ? game.BuildingCost(me, b) : info.Nuke is { } n ? game.NukeCost(n) : 0;
            GameHud.SetText(_cost, "$" + GameHud.FormatCount(cost));
        }
    }
}
