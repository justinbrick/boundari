using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The leaderboard (top players by land, plus your own row) and the player info panel
/// that shows whoever the mouse is over. Both are the panels built in boundari.tscn;
/// this only fills in their labels. Missing nodes are skipped.
/// </summary>
public sealed class PlayerPanels
{
    private const string Leader = "Main_Layer/Main_Control/Leader_Rect/";
    private const string Info = "Main_Layer/Main_Control/Display_Rec/";
    private const string InfoBox = Info + "Panel/Control/VBoxContainer/";
    private const double LeaderboardRefreshSeconds = 0.5;

    private sealed class Row
    {
        public Label? Pos, Name, Percent, Troops, Cash;
    }

    private static readonly string[] RowNodes = { "1st", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th" };

    private readonly List<Row> _rows = new();
    private readonly Row? _myRow;

    private readonly Control? _info;
    private readonly TextureRect? _infoFlag;
    private readonly Label? _infoName, _infoPlace, _infoLand, _infoCash, _infoTroops, _infoMaxTroops, _infoAttacking;
    private readonly List<(BuildingType Type, Label Label)> _infoBuildings = new();

    private readonly Dictionary<string, Texture2D?> _flags = new();
    private double _sinceRefresh = LeaderboardRefreshSeconds;
    private Dictionary<ushort, int> _ranks = new();

    public PlayerPanels(Node root)
    {
        foreach (string row in RowNodes)
            if (ReadRow(root.GetNodeOrNull(Leader + "VBoxContainer/ScrollContainer/VBoxContainer/" + row)) is { } r)
                _rows.Add(r);
        _myRow = ReadRow(root.GetNodeOrNull(Leader + "Personal_Panel/3rd"));

        _info = root.GetNodeOrNull<Control>(Info.TrimEnd('/'));
        _infoFlag = root.GetNodeOrNull<TextureRect>(InfoBox + "HBoxContainer/Flag");
        _infoName = root.GetNodeOrNull<Label>(InfoBox + "HBoxContainer/Name");
        _infoPlace = root.GetNodeOrNull<Label>(InfoBox + "HBoxContainer/Place");
        _infoLand = root.GetNodeOrNull<Label>(InfoBox + "Panel_Land/HBoxContainer/HBoxContainer2/Land_Amount");
        _infoCash = root.GetNodeOrNull<Label>(InfoBox + "Panel_Cash/HBoxContainer/Cash_Amount");
        _infoTroops = root.GetNodeOrNull<Label>(InfoBox + "Panel_Pop/HBoxContainer/HBoxContainer2/Pop1");
        _infoMaxTroops = root.GetNodeOrNull<Label>(InfoBox + "Panel_Pop/HBoxContainer/HBoxContainer2/Pop2");
        _infoAttacking = root.GetNodeOrNull<Label>(InfoBox + "Panel_Attacking/HBoxContainer/Attack_Amount");

        // Building counts: each cell in the panel is named after its building.
        foreach (string row in new[] { "HBox_Row1", "HBox_Row2", "HBox_Row3" })
            if (root.GetNodeOrNull(InfoBox + row) is { } r)
                foreach (var cell in r.GetChildren())
                    if (Enum.TryParse<BuildingType>(cell.Name, true, out var type) && cell.GetNodeOrNull<Label>("Label") is { } label)
                        _infoBuildings.Add((type, label));
        if (_info is not null)
            _info.Visible = false;
    }

    /// <summary>
    /// The leaderboard's labels in a row, matched by name. Rows were built by hand, so
    /// the first row uses descriptive names and the others Label..Label5.
    /// </summary>
    private static Row? ReadRow(Node? row)
    {
        if (row is null)
            return null;
        Label? Get(string a, string b) => row.GetNodeOrNull<Label>(a) ?? row.GetNodeOrNull<Label>(b);
        return new Row
        {
            Pos = Get("Pos", "Label5"),
            Name = Get("Name", "Label"),
            Percent = Get("Percent", "Label2"),
            Troops = Get("Pop", "Label3"),
            Cash = Get("Cash", "Label4"),
        };
    }

    public void Update(Game game, Player me, Player? hovered, double delta)
    {
        if ((_sinceRefresh += delta) >= LeaderboardRefreshSeconds)
        {
            _sinceRefresh = 0;
            UpdateLeaderboard(game, me);
        }
        UpdateInfo(game, hovered);
    }

    private void UpdateLeaderboard(Game game, Player me)
    {
        var ranked = game.Players.Where(p => p.HasSpawned && p.TileCount > 0)
            .OrderByDescending(p => p.TileCount).ThenBy(p => p.Id).ToList();
        _ranks = new Dictionary<ushort, int>(ranked.Count);
        for (int i = 0; i < ranked.Count; i++)
            _ranks[ranked[i].Id] = i + 1;

        long land = Math.Max(1, game.Map.LandTileCount);
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool used = i < ranked.Count;
            if (row.Pos?.GetParent() is CanvasItem rowNode)
                rowNode.Visible = used;
            if (used)
                Fill(row, i + 1, ranked[i], land, ranked[i] == me);
        }
        if (_myRow is not null)
        {
            // The pinned "your rank" row sits in its own panel (row's parent's parent).
            if (_myRow.Pos?.GetParent()?.GetParent() is CanvasItem panel)
                panel.Visible = _ranks.ContainsKey(me.Id);
            if (_ranks.TryGetValue(me.Id, out int rank))
                Fill(_myRow, rank, me, land, true);
        }
    }

    private static void Fill(Row row, int rank, Player p, long land, bool isMe)
    {
        Set(row.Pos, rank.ToString(CultureInfo.InvariantCulture));
        Set(row.Name, p.Name);
        // Your own row stands out in gold.
        var color = isMe ? new Color(1f, 0.85f, 0.4f) : new Color(1, 1, 1);
        foreach (var label in new[] { row.Pos, row.Name, row.Percent, row.Troops, row.Cash })
            label?.AddThemeColorOverride("font_color", color);
        Set(row.Percent, Percent(p.TileCount, land));
        Set(row.Troops, GameHud.FormatCount(p.Troops));
        Set(row.Cash, GameHud.FormatCount(p.Gold));
    }

    private void UpdateInfo(Game game, Player? p)
    {
        if (_info is null)
            return;
        _info.Visible = p is not null && p.TileCount > 0;
        if (!_info.Visible)
            return;

        if (_infoFlag is not null)
            _infoFlag.Texture = Flag(p!.Flag);
        Set(_infoName, p!.Name);
        Set(_infoPlace, _ranks.TryGetValue(p.Id, out int rank) ? Ordinal(rank) : "");
        string percent = Percent(p.TileCount, Math.Max(1, game.Map.LandTileCount));
        Set(_infoLand, percent.TrimEnd('%'));
        Set(_infoCash, "$" + GameHud.FormatCount(p.Gold));
        Set(_infoTroops, GameHud.FormatCount(p.Troops));
        Set(_infoMaxTroops, GameHud.FormatCount(p.MaxTroops));
        long attacking = 0;
        foreach (var a in game.Attacks)
            if (a.Attacker == p)
                attacking += a.Troops;
        Set(_infoAttacking, GameHud.FormatCount(attacking));
        foreach (var (type, label) in _infoBuildings)
            Set(label, game.CountBuildings(p, type).ToString(CultureInfo.InvariantCulture));
    }

    private Texture2D? Flag(string code)
    {
        if (string.IsNullOrEmpty(code))
            return null;
        if (!_flags.TryGetValue(code, out var texture))
        {
            string path = $"res://ui/flags - small/{code}.png";
            texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
            _flags[code] = texture;
        }
        return texture;
    }

    private static string Percent(long tiles, long land)
    {
        double pct = 100.0 * tiles / land;
        return (pct < 10 ? pct.ToString("0.0", CultureInfo.InvariantCulture) : pct.ToString("0", CultureInfo.InvariantCulture)) + "%";
    }

    private static string Ordinal(int n)
    {
        string suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    private static void Set(Label? label, string text)
    {
        if (label is not null && label.Text != text)
            label.Text = text;
    }
}
