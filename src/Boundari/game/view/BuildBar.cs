using System.Collections.Generic;
using System.Globalization;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The build buttons (ButtonsRow in boundari.tscn): each button's picture lifts and brightens
/// while hovered (and stays up while it's the one you're placing or aiming), darkens while
/// pressed, and the number on it is how many of that structure you have (levels count, like
/// OpenFront's bar). Buttons without a Label child (the bombs) just lift.
/// While a button is hovered, the attack ratio row above them (AttackRatio_Hbox) gives way to
/// Description_HBox: that structure's icon, name, what it does and its current price.
/// </summary>
public sealed class BuildBar
{
    private const string Row = "Main_Layer/Main_Control/ButtonsRow";
    private const float LiftPixels = 12;
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
    private readonly List<Lift> _lifts = new();

    // The description row and the attack ratio row it takes the place of.
    private readonly Control? _ratioRow, _descriptionRow;
    private readonly TextureRect? _icon;
    private readonly Label? _name, _description, _cost;
    private Info? _hovered;

    /// <summary>The mouse is on the Flak button or a bomb's (OpenFront then shows every Flak's range).</summary>
    public bool HoveringFlakOrBomb => _hovered is { } info && (info.Building == BuildingType.Sam || info.Nuke is not null);

    public BuildBar(Node root)
    {
        _ratioRow = GameHud.FindInHud(root, "AttackRatio_Hbox");
        _descriptionRow = GameHud.FindInHud(root, "Description_HBox");
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
            Buttons.TryGetValue(button.Name, out var info);
            _lifts.Add(new Lift(button, picture, info));
            if (info is null)
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

    /// <summary>
    /// One button's picture: up and brighter while hovered or selected, back down otherwise,
    /// and darker while the mouse button is held on it. The brightness comes from the
    /// button shader's hover_amount and pressed_amount (buttonoverhaul.gdshader).
    /// </summary>
    private sealed class Lift
    {
        public readonly Info? Info;
        public readonly Button Button;
        private readonly Control _picture;
        private readonly ShaderMaterial? _shader;
        private readonly float _top, _bottom;
        private bool _hovered, _selected;
        private Tween? _tween;

        public Lift(Button button, Control picture, Info? info)
        {
            Info = info;
            Button = button;
            _picture = picture;
            // Offsets, not position: they are what the scene sets, so they stay right when the window resizes.
            _top = picture.OffsetTop;
            _bottom = picture.OffsetBottom;
            // The scene's buttons reach above their pictures, so the mouse lifted them from
            // empty space: the button now starts where its picture does (the picture is
            // pinned to the button's bottom, so it stays put).
            if (picture.AnchorTop == 1 && picture.AnchorBottom == 1 && button.AnchorTop == button.AnchorBottom
                && button.OffsetBottom + picture.OffsetTop > button.OffsetTop)
                button.OffsetTop = button.OffsetBottom + picture.OffsetTop;
            // Its own copy, so lighting one button never lights another that shares the material.
            if (picture.Material is ShaderMaterial shared && HasParam(shared, "hover_amount"))
                picture.Material = _shader = (ShaderMaterial)shared.Duplicate();
            button.MouseEntered += () => { _hovered = true; Refresh(); };
            button.MouseExited += () => { _hovered = false; Refresh(); };
            button.ButtonDown += () => _shader?.SetShaderParameter("pressed_amount", 1f);
            button.ButtonUp += () => _shader?.SetShaderParameter("pressed_amount", 0f);
        }

        private static bool HasParam(ShaderMaterial material, string name)
        {
            if (material.Shader is null)
                return false;
            foreach (var u in material.Shader.GetShaderUniformList())
                if (u.AsGodotDictionary()["name"].AsString() == name)
                    return true;
            return false;
        }

        private float _dim = -1;

        public void Dim(float amount)
        {
            if (_dim == amount || _shader is null)
                return;
            _dim = amount;
            if (HasParam(_shader, "dim_amount"))
                _shader.SetShaderParameter("dim_amount", amount);
        }

        public bool Selected
        {
            set
            {
                if (_selected == value)
                    return;
                _selected = value;
                Refresh();
            }
        }

        private void Refresh()
        {
            bool up = _hovered || _selected;
            _tween?.Kill();
            _tween = _picture.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            _tween.TweenProperty(_picture, "offset_top", _top - (up ? LiftPixels : 0), LiftSeconds);
            _tween.TweenProperty(_picture, "offset_bottom", _bottom - (up ? LiftPixels : 0), LiftSeconds);
            if (_shader is not null)
                _tween.TweenProperty(_shader, "shader_parameter/hover_amount", up ? 1f : 0f, LiftSeconds);
        }
    }

    private const float DimAmount = 0.25f; // how much darker a button you can't afford or use is drawn

    /// <summary>Dims (or undims) a button's picture: its shader ignores the button's own tint.</summary>
    public void SetUsable(Button button, bool usable)
    {
        foreach (var lift in _lifts)
            if (lift.Button == button)
                lift.Dim(usable ? 0 : DimAmount);
    }

    private bool _mouseBlocked;

    /// <summary>
    /// While the attack ratio slider is held the buttons ignore the mouse (no lifting, no
    /// description); afterwards they listen again.
    /// </summary>
    public void BlockMouse(bool blocked)
    {
        if (_mouseBlocked == blocked)
            return;
        _mouseBlocked = blocked;
        foreach (var lift in _lifts)
            lift.Button.MouseFilter = blocked ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
    }

    /// <summary>The structure being placed or the bomb being aimed keeps its button up.</summary>
    public void SetSelected(BuildingType? placing, NukeType? aiming)
    {
        foreach (var lift in _lifts)
            lift.Selected = lift.Info is { } info
                && ((placing is not null && info.Building == placing) || (aiming is not null && info.Nuke == aiming));
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
