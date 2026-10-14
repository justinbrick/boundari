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

    private readonly Label? _troops;
    private readonly Label? _maxTroops;
    private readonly Label? _troopRate;
    private readonly Label? _cash;
    private readonly Label? _attackTroops;
    private readonly HSlider? _ratio;
    private readonly Label _status;

    public GameHud(Node root)
    {
        _troops = root.GetNodeOrNull<Label>(Info + "Panel_Pop/HBoxContainer/Pop1");
        _maxTroops = root.GetNodeOrNull<Label>(Info + "Panel_Pop/HBoxContainer/Pop2");
        _troopRate = root.GetNodeOrNull<Label>(Info + "Panel_Pop/HBoxContainer/Pop_Rate");
        _cash = root.GetNodeOrNull<Label>(Info + "Panel_Cur/HBoxContainer/Cur");
        _attackTroops = root.GetNodeOrNull<Label>(Info + "Ratio_Panel/HBoxContainer2/Amount");
        _ratio = root.GetNodeOrNull<HSlider>(Info + "Ratio_Panel/Slider_Control/VBox_Slider/HSlider");

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

    /// <summary>Share of troops to send when attacking, from the HUD slider (1..100).</summary>
    public int AttackRatioPercent => Mathf.Clamp((int)(_ratio?.Value ?? 35), 1, 100);

    public void Update(Game game, Player me)
    {
        Set(_troops, FormatCount(me.Troops));
        Set(_maxTroops, FormatCount(me.MaxTroops));
        Set(_troopRate, (me.TroopsPerSecond >= 0 ? "+" : "") + me.TroopsPerSecond.ToString("N0", CultureInfo.InvariantCulture) + "/s");
        Set(_cash, "$" + me.Gold.ToString("N0", CultureInfo.InvariantCulture));
        Set(_attackTroops, FormatCount(me.Troops * AttackRatioPercent / 100));

        _status.Text = game.Phase switch
        {
            GamePhase.Spawning when !me.HasSpawned => $"Click on land to choose where you start  ·  {game.SecondsUntilStart}s",
            GamePhase.Spawning => $"Starting in {game.SecondsUntilStart}s  ·  click again to move",
            _ when !me.HasSpawned => "Spectating",
            _ when !me.IsAlive => "You have been defeated",
            _ => "",
        };
    }

    private static void Set(Label? label, string text)
    {
        if (label is not null && label.Text != text)
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
