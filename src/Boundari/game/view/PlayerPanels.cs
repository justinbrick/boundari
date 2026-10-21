using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The leaderboard (top players by land, plus your own row) and the player info panel
/// that shows whoever the mouse is over ("Wilderness" over unclaimed land). Both are the
/// panels built in boundari.tscn; this only fills in their labels. Missing nodes are skipped.
/// </summary>
public sealed class PlayerPanels
{
    private const string Leader = "Main_Layer/Main_Control/Leader_Rect/";
    private const string Info = "Main_Layer/Main_Control/Display_Rec/";
    private const double LeaderboardRefreshSeconds = 0.5;

    private sealed class Row
    {
        public Label? Pos, Name, Percent, Troops, Cash;
        public bool? ShownAsMe; // colour last applied (re-colouring makes Godot redo the theme)
    }

    // The hover panel counts buildings across the map, so it refreshes 10 times a second
    // (or at once when the mouse moves to someone else).
    private const double InfoRefreshSeconds = 0.1;
    private double _sinceInfo = InfoRefreshSeconds;
    private Player? _infoShown;
    private bool _wildernessShown;

    private static readonly string[] RowNodes = { "1st", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th" };

    private readonly List<Row> _rows = new();
    private readonly Row? _myRow;

    private readonly Control? _info;
    private readonly TextureRect? _infoFlag;
    private readonly Label? _infoName, _infoPlace, _infoLand, _infoCash, _infoTroops, _infoMaxTroops;
    private readonly Label? _infoRelation, _infoTrade, _infoType, _infoAllianceTime;
    private readonly float _nameMinWidth; // the Name label's width in the scene: names get at least this, and more if they fit
    private readonly TroopMeterFill? _infoMeter;
    private string? _relationShown, _tradeShown;

    // Relation colours: allies and friends green, neutral plain, distrustful orange, hostile red.
    private static readonly Color AlliesColour = new(0.472f, 1f, 0.44f); // the scene's green
    private static readonly Color FriendlyColour = new(0.62f, 0.86f, 0.5f);
    private static readonly Color NeutralColour = new(1, 1, 1);
    private static readonly Color DistrustfulColour = new(1f, 0.6f, 0.2f);
    private static readonly Color HostileColour = new(1f, 0.32f, 0.28f);
    // Structure counts above 0: a little short of full white, which looked harsh.
    private static readonly Color LitColour = new(0.85f, 0.85f, 0.85f);
    // Structure counts: the number, its icon, and the dim look they have in the scene (used while it's 0).
    private readonly List<(BuildingType Type, Label Label, CanvasItem Icon, Color DimLabel, Color DimIcon)> _infoBuildings = new();

    private readonly Dictionary<string, Texture2D?> _flags = new();
    private double _sinceRefresh = LeaderboardRefreshSeconds;
    private Dictionary<ushort, int> _ranks = new();

    public PlayerPanels(Node root)
    {
        foreach (string row in RowNodes)
            if (ReadRow(root.GetNodeOrNull(Leader + "VBoxContainer/ScrollContainer/VBoxContainer/" + row)) is { } r)
                _rows.Add(r);
        _myRow = ReadRow(root.GetNodeOrNull(Leader + "Personal_Panel/3rd"));

        // The hover panel (Display_Rec), found by what's in it rather than exact paths, so
        // it keeps working while it's being laid out:
        //   the flag, with the name and place beside it;
        //   rows of "<title> <answer>" for Trade, Relation and Type (the answer is the row's
        //   other label, e.g. Relation_Answer);
        //   Land_Amount (land %);
        //   a box holding Current_Troops and Max_Troops (the troop meter) and a box with a
        //   single label (gold);
        //   structure counts: an icon and a number each, the building told by its icon.
        _info = root.GetNodeOrNull<Control>(Info.TrimEnd('/'));
        if (_info is not null)
        {
            _infoFlag = _info.FindChild("Flag", true, false) as TextureRect;
            _infoName = _infoFlag?.GetParent().GetNodeOrNull<Label>("Name");
            _infoPlace = _infoFlag?.GetParent().GetNodeOrNull<Label>("Placement") ?? _infoFlag?.GetParent().GetNodeOrNull<Label>("Place");
            _nameMinWidth = _infoName?.CustomMinimumSize.X ?? 0;
            _infoLand = _info.FindChild("Land_Amount", true, false) as Label;
            _infoAllianceTime = _info.FindChild("Alliance_Time", true, false) as Label;
            foreach (var row in Descendants(_info))
            {
                if (row is not HBoxContainer box)
                    continue;
                var labels = new List<Label>();
                foreach (var c in box.GetChildren())
                    if (c is Label l)
                        labels.Add(l);
                if (labels.Count < 2)
                    continue;
                switch (labels[0].Text.Trim())
                {
                    case "Trade": _infoTrade = labels[1]; break;
                    case "Relation": _infoRelation = labels[1]; break; // (then Alliance_Time)
                    case "Type": _infoType = labels[1]; break;
                }
            }
            foreach (var node in Descendants(_info))
            {
                if (node is not Panel panel)
                    continue;
                var current = panel.FindChild("Current_Troops", true, false) as Label;
                var max = panel.FindChild("Max_Troops", true, false) as Label;
                if (current is not null && max is not null)
                {
                    _infoTroops = current;
                    _infoMaxTroops = max;
                    _infoMeter = new TroopMeterFill { Name = "Fill", MouseFilter = Control.MouseFilterEnum.Ignore };
                    _infoMeter.SetAnchorsPreset(Control.LayoutPreset.FullRect);
                    panel.AddChild(_infoMeter);
                    panel.MoveChild(_infoMeter, 0); // behind the numbers
                    if (panel.GetThemeStylebox("panel") is StyleBoxFlat style)
                        _infoMeter.MatchPanel(style);
                }
                else if (current is not null && panel.Name.ToString().StartsWith("Display", StringComparison.Ordinal))
                    _infoCash = current; // the one-number box: gold
            }
            foreach (var node in Descendants(_info))
                if (node is TextureRect icon && icon != _infoFlag && icon.Texture?.ResourcePath is { } path
                    && BuildingFromIcon(path) is { } type && icon.GetParent().GetNodeOrNull<Label>("Label") is { } count)
                    _infoBuildings.Add((type, count, icon, count.Modulate, icon.Modulate));
            _info.Visible = false;
        }
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

    /// <summary>Whether this control is (part of) the hover panel: the mouse looks through it at the map.</summary>
    public bool Contains(Control control) => _info is not null && (control == _info || _info.IsAncestorOf(control));

    /// <param name="wilderness">The mouse is over unclaimed land (with no <paramref name="hovered"/> player).</param>
    public void Update(Game game, Player me, Player? hovered, bool wilderness, double delta)
    {
        if ((_sinceRefresh += delta) >= LeaderboardRefreshSeconds)
        {
            _sinceRefresh = 0;
            long started = FrameProfiler.Start();
            UpdateLeaderboard(game, me);
            FrameProfiler.Stop("  leaderboard", started);
        }
        wilderness &= hovered is null;
        if ((_sinceInfo += delta) >= InfoRefreshSeconds || hovered != _infoShown || wilderness != _wildernessShown)
        {
            _sinceInfo = 0;
            _infoShown = hovered;
            _wildernessShown = wilderness;
            long started = FrameProfiler.Start();
            UpdateInfo(game, me, hovered, wilderness);
            FrameProfiler.Stop("  hover panel", started);
        }
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
            if (row.Pos?.GetParent() is CanvasItem rowNode && rowNode.Visible != used)
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
        if (row.ShownAsMe != isMe)
        {
            row.ShownAsMe = isMe;
            var color = isMe ? new Color(1f, 0.85f, 0.4f) : new Color(1, 1, 1);
            foreach (var label in new[] { row.Pos, row.Name, row.Percent, row.Troops, row.Cash })
                label?.AddThemeColorOverride("font_color", color);
        }
        Set(row.Percent, Percent(p.TileCount, land));
        Set(row.Troops, GameHud.FormatCount(p.Troops));
        Set(row.Cash, GameHud.FormatCount(p.Gold));
    }

    private void UpdateInfo(Game game, Player me, Player? p, bool wilderness)
    {
        if (_info is null)
            return;
        bool visible = wilderness || (p is not null && p.TileCount > 0);
        if (_info.Visible != visible)
            _info.Visible = visible;
        if (!visible)
            return;
        if (wilderness)
        {
            ShowWilderness(game);
            return;
        }

        if (_infoFlag is not null && _infoFlag.Texture != Flag(p!.Flag))
            _infoFlag.Texture = Flag(p!.Flag);
        if (_infoFlag is not null && !_infoFlag.Visible)
            _infoFlag.Visible = true;
        Set(_infoName, p!.Name);
        Set(_infoPlace, _ranks.TryGetValue(p.Id, out int rank) ? Ordinal(rank) : "");
        FitName();
        string percent = Percent(p.TileCount, Math.Max(1, game.Map.LandTileCount));
        Set(_infoLand, percent.TrimEnd('%'));
        Set(_infoCash, p.InfiniteGold ? "$∞" : "$" + GameHud.FormatCount(p.Gold));
        Set(_infoTroops, GameHud.FormatCount(p.Troops));
        Set(_infoMaxTroops, GameHud.FormatCount(p.MaxTroops));
        long attacking = 0;
        foreach (var a in game.Attacks)
            if (a.Attacker == p)
                attacking += a.Troops;
        if (_infoMeter is not null)
        {
            // Like the troop meter at the bottom: troops at home, then those out attacking.
            double cap = Math.Max(p.MaxTroops, 1);
            float home = (float)Math.Clamp(p.Troops / cap, 0, 1);
            float away = (float)Math.Clamp(attacking / cap, 0, 1 - home);
            _infoMeter.SetTarget(home, away, snap: p != _meterShows);
            _meterShows = p;
        }
        Set(_infoType, p.Kind switch { PlayerKind.Nation => "Nation", PlayerKind.Bot => "Tribe", _ => "Player" });
        ShowRelation(game, me, p);
        ShowTrade(p == me ? "-" : game.CanTrade(me, p) ? "Active" : "Halted");
        foreach (var (type, label, icon, dimLabel, dimIcon) in _infoBuildings)
            ShowCount(label, icon, dimLabel, dimIcon, type == BuildingType.Warship ? game.CountWarships(p) : game.CountLevels(p, type));
    }

    /// <summary>Unclaimed land: how much of the map is still free, and dashes for the rest.</summary>
    private void ShowWilderness(Game game)
    {
        if (_infoFlag is not null && _infoFlag.Visible)
            _infoFlag.Visible = false; // no flag: the name moves up to the edge
        Set(_infoName, "Wilderness");
        Set(_infoPlace, "");
        FitName();
        ShowAllianceTime(null);
        long land = Math.Max(1, game.Map.LandTileCount), claimed = 0;
        foreach (var player in game.Players)
            claimed += player.TileCount;
        Set(_infoLand, Percent(Math.Max(0, land - claimed), land).TrimEnd('%'));
        Set(_infoCash, "-");
        Set(_infoTroops, "0");
        Set(_infoMaxTroops, "0");
        _infoMeter?.SetTarget(0, 0, snap: true);
        _meterShows = null;
        Set(_infoType, "Unclaimed");
        if (_infoRelation is not null)
        {
            Set(_infoRelation, "-");
            if (_relationShown != "-")
            {
                _relationShown = "-";
                _infoRelation.AddThemeColorOverride("font_color", NeutralColour);
            }
        }
        ShowTrade("-");
        foreach (var (_, label, icon, dimLabel, dimIcon) in _infoBuildings)
            ShowCount(label, icon, dimLabel, dimIcon, 0);
    }

    private void ShowTrade(string trade)
    {
        Set(_infoTrade, trade);
        if (_infoTrade is not null && _tradeShown != trade)
        {
            _tradeShown = trade;
            _infoTrade.AddThemeColorOverride("font_color", trade switch { "Halted" => DistrustfulColour, "-" => NeutralColour, _ => AlliesColour });
        }
    }

    /// <summary>A structure count: lit once they have one (like the bottom bar), the scene's dim look at 0.</summary>
    private static void ShowCount(Label label, CanvasItem icon, Color dimLabel, Color dimIcon, int n)
    {
        Set(label, n.ToString(CultureInfo.InvariantCulture));
        var lit = n > 0 ? LitColour : dimLabel;
        if (label.Modulate != lit)
            label.Modulate = lit;
        var litIcon = n > 0 ? LitColour : dimIcon;
        if (icon.Modulate != litIcon)
            icon.Modulate = litIcon;
    }

    /// <summary>How the hovered player feels about you (as OpenFront shows it), or "Allies".</summary>
    private void ShowRelation(Game game, Player me, Player p)
    {
        if (_infoRelation is null)
            return;
        var (text, colour) = p == me ? ("-", NeutralColour)
            : me.IsAlliedWith(p) ? ("Allies", AlliesColour)
            : game.RelationOf(p, me) switch
            {
                Relation.Hostile => ("Hostile", HostileColour),
                Relation.Distrustful => ("Distrustful", DistrustfulColour),
                Relation.Friendly => ("Friendly", FriendlyColour),
                _ => ("Neutral", NeutralColour),
            };
        Set(_infoRelation, text);
        ShowAllianceTime(p == me ? null : game.AllianceBetween(me, p) is { } alliance ? alliance.ExpiresTick - game.Tick : null);
        if (_relationShown != text) // re-colouring makes Godot redo the theme: only when it changes
        {
            _relationShown = text;
            _infoRelation.AddThemeColorOverride("font_color", colour);
        }
    }

    /// <summary>Time left on your alliance with them ("1m 30s"), hidden when you're not allied.</summary>
    private void ShowAllianceTime(int? ticksLeft)
    {
        if (_infoAllianceTime is null)
            return;
        bool show = ticksLeft is not null;
        if (_infoAllianceTime.Visible != show)
            _infoAllianceTime.Visible = show;
        if (ticksLeft is not { } ticks)
            return;
        int seconds = Math.Max(0, (ticks + Balance.TicksPerSecond - 1) / Balance.TicksPerSecond);
        Set(_infoAllianceTime, seconds >= 60 ? $"{seconds / 60}m {seconds % 60}s" : $"{seconds}s");
    }

    /// <summary>
    /// Widens the Name label to fit the whole name when there's room beside the flag and
    /// the place (the scene's width is the least it gets); longer names still end in "...".
    /// </summary>
    private void FitName()
    {
        if (_infoName?.GetParent() is not Control row || row.Size.X <= 0)
            return;
        var font = _infoName.GetThemeFont("font");
        int size = _infoName.GetThemeFontSize("font_size");
        float wanted = font.GetStringSize(_infoName.Text, HorizontalAlignment.Left, -1, size).X + 2;
        float others = 0;
        int shown = 0;
        foreach (var child in row.GetChildren())
            if (child is Control c && c.Visible && c != _infoName)
            {
                others += c == _infoPlace && _infoPlace is not null
                    ? _infoPlace.GetThemeFont("font").GetStringSize(_infoPlace.Text, HorizontalAlignment.Left, -1, _infoPlace.GetThemeFontSize("font_size")).X
                    : c.GetCombinedMinimumSize().X;
                shown++;
            }
        float room = row.Size.X - others - shown * row.GetThemeConstant("separation");
        float width = Math.Max(_nameMinWidth, Math.Min(wanted, room));
        if (Math.Abs(_infoName.CustomMinimumSize.X - width) > 0.5f)
            _infoName.CustomMinimumSize = new Vector2(width, _infoName.CustomMinimumSize.Y);
    }

    private Player? _meterShows; // whose troops the hover meter shows (a new player snaps, no glide)

    /// <summary>Which building a structure icon stands for, from its file name (harbour32x.svg, sam2.svg...).</summary>
    private static BuildingType? BuildingFromIcon(string path)
    {
        string file = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return file.StartsWith("harbour") || file.StartsWith("port") ? BuildingType.Port
            : file.StartsWith("barracks") || file.StartsWith("city") ? BuildingType.City
            : file.StartsWith("depot") || file.StartsWith("factory") ? BuildingType.Factory
            : file.StartsWith("defense") ? BuildingType.Defense
            : file.StartsWith("silo") ? BuildingType.Silo
            : file.StartsWith("sam") || file.StartsWith("flak") ? BuildingType.Sam
            : file.StartsWith("warship") ? BuildingType.Warship
            : null;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var deeper in Descendants(child))
                yield return deeper;
        }
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

    private static void Set(Label? label, string text) => GameHud.SetText(label, text);
}
