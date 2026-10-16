using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Attacks and boats, like OpenFront's attacks list: one tab each with the troops and who
/// it's from or where it's going. Incoming ones (attacks on you, boats heading for your
/// coast) come first, in red, saying who they're from; your own outgoing ones follow, with
/// a button to call them off ("Retreating..." until the troops are home). The tabs are the
/// ones built in boundari.tscn (Control_Outgoing: rows of three); more rows are copied from
/// the first as needed.
/// </summary>
public sealed class OutgoingAttacks
{
    private const string Path = "Main_Layer/Main_Control/Control_Outgoing/VBoxContainer";

    private sealed class Tab
    {
        public Control Root = null!;
        public Label? Troops, Destination;
        public Button? Close;
        public bool IsBoat;
        public int Id = -1; // the attack or boat shown, -1 = unused
        public bool? ShownIncoming; // how the tab is currently styled
        public Color DestinationColour, TroopsColour;
    }

    private readonly Action<Intent> _submit;
    private readonly VBoxContainer? _rows;
    private readonly Control? _template;
    private readonly List<Control> _rowNodes = new();
    private readonly List<Tab> _tabs = new();
    private readonly List<(bool Incoming, bool IsBoat, int Id, long Troops, string Where, bool Retreating)> _items = new();
    private static readonly Color IncomingColour = new(1f, 0.42f, 0.38f);
    private Player? _me;

    /// <param name="submit">Sends an intent to the game (from the main thread).</param>
    public OutgoingAttacks(Node root, Action<Intent> submit)
    {
        _submit = submit;
        _rows = root.GetNodeOrNull<VBoxContainer>(Path);
        if (_rows is null)
            return;
        // It may be hidden in the editor (to keep it out of the way while designing): the
        // game shows it; empty rows hide themselves.
        if (_rows.GetParent() is CanvasItem area)
            area.Visible = true;
        foreach (var child in _rows.GetChildren())
            if (child is HBoxContainer row)
            {
                _template ??= (Control)row.Duplicate(); // a clean copy, for more rows later
                AddRow(row);
            }
    }

    private void AddRow(Control row)
    {
        _rowNodes.Add(row);
        foreach (var child in row.GetChildren())
        {
            if (child is not Control tabRoot)
                continue;
            var tab = new Tab
            {
                Root = tabRoot,
                Troops = tabRoot.FindChild("Troop_Count", true, false) as Label,
                Destination = tabRoot.FindChild("Destination", true, false) as Label,
                Close = tabRoot.FindChild("Button_Retreat", true, false) as Button,
            };
            tab.DestinationColour = tab.Destination?.GetThemeColor("font_color") ?? Colors.White;
            tab.TroopsColour = tab.Troops?.GetThemeColor("font_color") ?? Colors.White;
            if (tab.Close is not null)
            {
                tab.Close.FocusMode = Control.FocusModeEnum.None; // keep Space/Enter for the game
                tab.Close.Pressed += () => CallOff(tab);
            }
            tabRoot.Visible = false;
            _tabs.Add(tab);
        }
        row.Visible = false;
    }

    private void CallOff(Tab tab)
    {
        if (_me is null || tab.Id < 0 || tab.ShownIncoming == true)
            return;
        _submit(tab.IsBoat ? new BoatRetreatIntent(_me.Id, tab.Id) : new RetreatIntent(_me.Id, tab.Id));
    }

    /// <summary>Refreshes the tabs from the game (main thread, game in sync).</summary>
    public void Update(Game game, Player me)
    {
        if (_rows is null)
            return;
        _me = me;
        _items.Clear();
        foreach (var a in game.Attacks)
            if (a.Attacker == me)
                _items.Add((false, false, a.Id, a.Troops, a.TargetId == Game.Nobody ? "Wilderness" : game.GetPlayer(a.TargetId)?.Name ?? "?", a.Retreating));
            else if (a.TargetId == me.Id)
                _items.Add((true, false, a.Id, a.Troops, "From " + a.Attacker.Name, a.Retreating));
        foreach (var b in game.Boats)
            if (b.Owner == me)
            {
                ushort owner = game.Owner[b.LandingTile];
                string where = owner == Game.Nobody ? "Wilderness" : game.GetPlayer(owner)?.Name ?? "?";
                _items.Add((false, true, b.Id, b.Troops, "Boat to " + where, b.Retreating));
            }
            else if (!b.Retreating && game.Owner[b.LandingTile] == me.Id)
                _items.Add((true, true, b.Id, b.Troops, "Boat from " + b.Owner.Name, false));
        // Incoming first, then oldest first, so tabs don't jump around.
        _items.Sort((x, y) => x.Incoming != y.Incoming ? y.Incoming.CompareTo(x.Incoming) : x.Id.CompareTo(y.Id));

        // Enough rows of three for everything.
        while (_tabs.Count < _items.Count && _template is not null)
        {
            var row = (Control)_template.Duplicate();
            _rows.AddChild(row);
            AddRow(row);
        }

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            bool used = i < _items.Count;
            if (tab.Root.Visible != used)
                tab.Root.Visible = used;
            if (!used)
            {
                tab.Id = -1;
                continue;
            }
            var item = _items[i];
            tab.IsBoat = item.IsBoat;
            tab.Id = item.Id;
            GameHud.SetText(tab.Troops, GameHud.FormatCount(item.Troops));
            GameHud.SetText(tab.Destination, item.Retreating ? "Retreating..." : item.Where);
            if (tab.ShownIncoming != item.Incoming)
            {
                // Incoming: red, and no X (you can't call off someone else's attack).
                tab.ShownIncoming = item.Incoming;
                tab.Destination?.AddThemeColorOverride("font_color", item.Incoming ? IncomingColour : tab.DestinationColour);
                tab.Troops?.AddThemeColorOverride("font_color", item.Incoming ? IncomingColour : tab.TroopsColour);
                if (tab.Close is not null)
                    tab.Close.Visible = !item.Incoming;
            }
            if (tab.Close is not null && tab.Close.Disabled != item.Retreating)
                tab.Close.Disabled = item.Retreating;
        }
        // A row shows while any of its tabs is in use.
        int perRow = _rowNodes.Count > 0 ? Math.Max(1, _tabs.Count / _rowNodes.Count) : 3;
        for (int r = 0; r < _rowNodes.Count; r++)
        {
            bool shown = r * perRow < _items.Count;
            if (_rowNodes[r].Visible != shown)
                _rowNodes[r].Visible = shown;
        }
    }
}
