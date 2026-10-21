using System.Collections.Generic;
using System.Globalization;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The build buttons (ButtonsRow in boundari.tscn): each button's picture lifts a little while
/// hovered and settles back after, and the number on it is how many of that structure you have
/// (levels count, like OpenFront's bar). Buttons without a Label child (the bombs) just lift.
/// </summary>
public sealed class BuildBar
{
    private const string Row = "Main_Layer/Main_Control/ButtonsRow";
    private const float LiftPixels = 6;
    private const double LiftSeconds = 0.12;

    // Building counts loop over every building on the map, so they refresh 10 times a second.
    private const double CountRefreshSeconds = 0.1;
    private double _sinceCounts = CountRefreshSeconds;

    // Which button counts which structure.
    private static readonly Dictionary<string, BuildingType> ButtonTypes = new()
    {
        ["Port_Button"] = BuildingType.Port,
        ["Barracks_Button"] = BuildingType.City,
        ["Fac_Button"] = BuildingType.Factory,
        ["Def_Button"] = BuildingType.Defense,
        ["Silo_Button"] = BuildingType.Silo,
        ["Sam_Button"] = BuildingType.Sam,
        ["Ship_Button"] = BuildingType.Warship,
    };

    private readonly List<(BuildingType Type, Label Label)> _counts = new();

    public BuildBar(Node root)
    {
        if (root.GetNodeOrNull(Row) is not { } row)
            return;
        foreach (var wrapper in row.GetChildren())
        {
            var button = wrapper as Button ?? FirstButton(wrapper);
            if (button?.GetNodeOrNull<Control>("TextureRect") is not { } picture)
                continue;
            AddLift(button, picture);            if (ButtonTypes.TryGetValue(button.Name, out var type) && picture.GetNodeOrNull<Label>("Label") is { } label)
                _counts.Add((type, label));
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
    }
}
