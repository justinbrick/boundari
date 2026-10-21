using System;
using System.Collections.Generic;
using System.Globalization;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Feeds live numbers into the HUD built in boundari.tscn (troops, troop rate, cash,
/// attack ratio) and shows a status line for the spawn phase. Nodes are looked up by
/// path and skipped if missing, so reworking the UI never crashes the game.
/// </summary>
public sealed class GameHud
{
    private const string Info = "Main_Layer/Main_Control/HBox_Info/";
    private const string Ratio = "Main_Layer/Main_Control/HBox_AHudTEST/In_Panel2/AttackRatio_Hbox";

    private readonly Label? _troops;
    private readonly Label? _maxTroops;
    private readonly Label? _troopRate;
    private readonly Label? _cash;
    private readonly Label? _cashRate;
    private readonly Label? _attackTroops;
    private readonly Label? _ratioPercent;
    private readonly HSlider? _ratio;
    private readonly AttackRatioSlider? _ratioTicks;
    private readonly Label _status;

    private const float SliderHeight = 54;

    public GameHud(Node root)
    {
        _troops = root.GetNodeOrNull<Label>(Info + "Panel_Pop/HBoxContainer/Pop1");
        _maxTroops = root.GetNodeOrNull<Label>(Info + "Panel_Pop/HBoxContainer/Pop2");
        _troopRate = root.GetNodeOrNull<Label>(Info + "Panel_Pop/HBoxContainer/Pop_Rate");
        _cash = root.GetNodeOrNull<Label>(Info + "Panel_Cur/HBoxContainer/Cur");
        _cashRate = root.GetNodeOrNull<Label>(Info + "Panel_Cur/HBoxContainer/Cur_Rate");
        // The attack ratio row: "20% (1.65K)" beside its slider, like OpenFront's control panel.
        if (root.GetNodeOrNull<Control>(Ratio) is { } ratioRow)
        {
            _ratio = ratioRow.GetNodeOrNull<HSlider>("HSlider");
            _ratioPercent = ratioRow.GetNodeOrNull<Label>("Percentage_Label") ?? ratioRow.GetNodeOrNull<Label>("Label");
            _attackTroops = ratioRow.GetNodeOrNull<Label>("TroopAmount_Label") ?? ratioRow.GetNodeOrNull<Label>("Label2");
        }
        else
        {
            _attackTroops = root.GetNodeOrNull<Label>(Info + "Ratio_Panel/HBoxContainer2/Amount");
            _ratio = root.GetNodeOrNull<HSlider>(Info + "Ratio_Panel/Slider_Control/VBox_Slider/HSlider");
        }
        if (_ratio is not null)
            _ratio.FocusMode = Control.FocusModeEnum.None; // so the arrow keys keep moving the map

        // The old tick slider sat on top of the build bar's titles (Infrastructure / Action /
        // Wartime), as wide as that strip; only used when the scene has no slider.
        if (_ratio is null && root.GetNodeOrNull<Control>("Main_Layer/Main_Control/HBox_AHudTEST/In_Panel/Struct_Titles_Panel") is { } titles)
        {
            // Hidden for now (the user's call); it still holds the ratio attacks use (20%).
            _ratioTicks = new AttackRatioSlider { Name = "AttackRatioSlider", AnchorRight = 1, OffsetTop = -SliderHeight, Visible = false };
            titles.AddChild(_ratioTicks);
        }

        // Status line along the top of the screen (spawn countdown, defeat, ...).
        _status = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0,
            AnchorRight = 1,
            OffsetTop = 70,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _status.AddThemeFontSizeOverride("font_size", 26);
        _status.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        _status.AddThemeConstantOverride("outline_size", 8);
        var layer = root.GetNodeOrNull("Main_Layer") ?? root;
        layer.AddChild(_status);
    }

    /// <summary>Share of troops to send when attacking, from the HUD slider (10..100).</summary>
    public int AttackRatioPercent => _ratioTicks?.Value ?? Mathf.Clamp((int)Math.Round(_ratio?.Value ?? 20), 1, 100);

    /// <summary>Shown in place of the usual status line while set (e.g. fast-forward progress).</summary>
    public string? Status { get; set; }

    // A short message in the status line (attack ratio, pause, game speed...), for a moment.
    private string? _flash;
    private ulong _flashUntil;

    public void Flash(string text, double seconds = 1.5)
    {
        _flash = text;
        _flashUntil = Time.GetTicksMsec() + (ulong)(seconds * 1000);
    }

    /// <summary>Changes the share of troops sent (the slider holds it), like OpenFront's T/Y keys.</summary>
    public void AdjustRatio(int byPercent)
    {
        if (_ratioTicks is not null)
            _ratioTicks.Value += byPercent;
        else if (_ratio is not null)
            _ratio.Value += byPercent; // the slider keeps it within its own min and max
        Flash($"Attack ratio {AttackRatioPercent}%");
    }

    public void Update(Game game, Player me)
    {
        Set(_troops, FormatCount(me.Troops));
        Set(_maxTroops, FormatCount(me.MaxTroops));
        Set(_troopRate, (me.TroopsPerSecond >= 0 ? "+" : "") + me.TroopsPerSecond.ToString("N0", CultureInfo.InvariantCulture) + "/s");
        Set(_cash, me.InfiniteGold ? "$∞" : "$" + me.Gold.ToString("N0", CultureInfo.InvariantCulture));
        long goldPerSecond = me.TileCount > 0 ? Balance.GoldPerTick(me) * Balance.TicksPerSecond : 0;
        Set(_cashRate, "+" + goldPerSecond.ToString("N0", CultureInfo.InvariantCulture) + "/s");
        long attackTroops = me.Troops * AttackRatioPercent / 100;
        if (_ratioPercent is not null)
        {
            Set(_ratioPercent, AttackRatioPercent.ToString(CultureInfo.InvariantCulture) + "%");
            Set(_attackTroops, "(" + FormatCount(attackTroops) + ")");
        }
        else
            Set(_attackTroops, FormatCount(attackTroops));

        _status.Text = Status ?? (_flash is not null && Time.GetTicksMsec() < _flashUntil ? _flash : null) ?? game.Phase switch
        {
            _ when !me.HasSpawned && me.LateSpawn => "Mid-game: click on land to drop in (you take that land)",
            GamePhase.Spawning when !me.HasSpawned => $"Click on land to choose where you start  ·  {game.SecondsUntilStart}s",
            GamePhase.Spawning => $"Starting in {game.SecondsUntilStart}s  ·  click again to move",
            _ when !me.HasSpawned => "Spectating",
            _ when !me.IsAlive => "You have been defeated",
            _ => "",
        };
    }

    private static void Set(Label? label, string text) => SetText(label, text);

    // What each label was last given: reading Label.Text back from the engine every frame
    // allocates a string, and setting it (even to the same text) makes Godot redo the layout.
    private static readonly Dictionary<Label, string> Shown = new();

    /// <summary>Sets a label's text only if it changed.</summary>
    public static void SetText(Label? label, string text)
    {
        if (label is null || (Shown.TryGetValue(label, out var old) && old == text))
            return;
        Shown[label] = text;
        label.Text = text;
    }

    /// <summary>950, 9.5K, 142K, 1.52M, 15.2M ...</summary>
    public static string FormatCount(long n)
    {
        var inv = CultureInfo.InvariantCulture;
        return n switch
        {
            < 1_000 => n.ToString(inv),
            < 10_000 => (n / 1000.0).ToString("0.#", inv) + "K",
            < 1_000_000 => (n / 1000).ToString(inv) + "K",
            < 10_000_000 => (n / 1_000_000.0).ToString("0.##", inv) + "M",
            _ => (n / 1_000_000.0).ToString("0.#", inv) + "M",
        };
    }
}
