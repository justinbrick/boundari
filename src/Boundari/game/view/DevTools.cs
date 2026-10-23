using System;
using System.Collections.Generic;
using Godot;

namespace Boundari.View;

/// <summary>
/// Visual dev tools: a compact strip in the top-right corner. Ctrl+Left/Right picks a setting,
/// Ctrl+Up/Down changes it. Choices are saved (user://devtools.cfg) and come back next run.
///   PSC patch scale        S (the map's own detail) / M (merged 2x2) / L (merged 4x4)
///   PMR patch morphing     1 (tight) .. 6: how far land-cover patches blend together
///   PSH patch shape        0 (square cells) / 60 (slightly rounded) / 100 (smooth)
///   OPA territory opacity  0 .. 100% in steps of 5
///   BRD border thickness   0 / .15 / .3 / .5 / .8
///   GRD coordinate grid    off / on (OpenFront's lettered grid, see CoordinateGrid.cs)
///   TOP topography         contour lines: off / every 100 / 250 / 500 m
///   LBL labels             on / off: territory names, flags and troop counts
///   TOL text outline       width of the outline round territory names and troop counts: 0 .. 3x the mockup's
///   PLY players            on / off: hide every territory, label, building and ship to see the bare map (also T)
/// Below the strip, an "Ally request" button (or Y): a random territory on screen asks you to ally.
/// </summary>
public partial class DevTools : CanvasLayer
{
    private sealed record Setting(string Abbr, string Name, string[] Labels, int Default, Action<int> Apply);

    private const string ConfigPath = "user://devtools.cfg";
    // Saved under this section name; changing it drops everyone's old choices (done when
    // the options or their defaults changed).
    private const string ConfigSection = "dev3";

    private MapView? _view;
    private readonly List<Setting> _settings;
    private readonly int[] _values;
    private int _selected;
    private RichTextLabel _strip = null!;
    private Label _hint = null!;

    /// <summary>The map these settings change (set before adding the node).</summary>
    public MapView? View { get => _view; set => _view = value; }

    /// <summary>Shows or hides everything players put on the map (territory, labels, buildings, ships).</summary>
    public Action<bool>? ShowPlayers { get; set; }

    /// <summary>Shows or hides the territory names, flags and troop counts.</summary>
    public Action<bool>? ShowLabels { get; set; }

    /// <summary>Width of the outline round territory names and troop counts, as a multiple of the mockup's (1).</summary>
    public Action<float>? TextOutlineWidth { get; set; }

    /// <summary>Shows or hides the coordinate grid.</summary>
    public Action<bool>? ShowGrid { get; set; }

    /// <summary>Switches the territory colour theme (0 Military, 1 Vibrant: OpenFront's).</summary>
    public Action<ColorTheme>? SetColorTheme { get; set; }

    /// <summary>Makes a random territory ask the player to ally (the "Ally request" button / Y).</summary>
    public Action? ForceAllianceRequest { get; set; }

    private MapView V => _view ?? throw new InvalidOperationException("DevTools.View isn't set");

    public DevTools()
    {
        Layer = 90;
        Name = "DevTools";
        _settings = new List<Setting>
        {
            new("PSC", "Patch scale", new[] { "S", "M", "L" }, 0, i => V.SetPatchScale(i)),
            new("PMR", "Patch morphing", new[] { "1", "2", "3", "4", "5", "6" }, 1,
                i => V.PatchMorph = i == 0 ? 0f : 0.6f + 0.2f * i), // 1 = tight B-spline blend; then wider
            new("PSH", "Patch shape (corner rounding)", new[] { "0", "60", "100" }, 2,
                i => V.PatchShape = new[] { 0f, 0.6f, 1f }[i]),
            new("OPA", "Territory opacity", OpacitySteps(), 11, i => V.TerritoryOpacity = i * 0.05f),
            new("BRD", "Territory border thickness", new[] { "0", ".15", ".3", ".5", ".8" }, 3,
                i => V.BorderWidth = new[] { 0f, 0.15f, 0.3f, 0.5f, 0.8f }[i]),
            new("GRD", "Coordinate grid", new[] { "OFF", "ON" }, 0, i => ShowGrid?.Invoke(i == 1)),
            new("TOP", "Topography (contour lines, metres)", new[] { "OFF", "100", "250", "500" }, 0,
                i => V.ContourInterval = new[] { 0f, 100f, 250f, 500f }[i]),
            new("COL", "Territory colours (military / vibrant: OpenFront's)", new[] { "MIL", "VIB" }, 0,
                i => SetColorTheme?.Invoke((ColorTheme)i)),
            new("LBL", "Territory names, flags and troops", new[] { "ON", "OFF" }, 0, i => ShowLabels?.Invoke(i == 0)),
            new("TOL", "Name and troop outline width (x the mockup's)", new[] { "0", ".5", ".75", "1", "1.5", "2", "3" }, 3,
                i => TextOutlineWidth?.Invoke(new[] { 0f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f }[i])),
            new("PLY", "Players, tribes and nations (F7 toggles)", new[] { "ON", "OFF" }, 0, i => ShowPlayers?.Invoke(i == 0)),
        };
        _values = new int[_settings.Count];
        for (int i = 0; i < _settings.Count; i++)
            _values[i] = _settings[i].Default;
        Load();
    }

    /// <summary>"0", "5", ... "100".</summary>
    private static string[] OpacitySteps()
    {
        var steps = new string[21];
        for (int i = 0; i < steps.Length; i++)
            steps[i] = (i * 5).ToString();
        return steps;
    }

    public override void _Ready()
    {
        var panel = new PanelContainer
        {
            AnchorLeft = 1, AnchorRight = 1, OffsetRight = -8, OffsetTop = 6,
            GrowHorizontal = Control.GrowDirection.Begin, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.06f, 0.72f),
            ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 3, ContentMarginBottom = 3,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);

        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 0);
        panel.AddChild(column);
        _strip = new RichTextLabel
        {
            BbcodeEnabled = true, FitContent = true, AutowrapMode = TextServer.AutowrapMode.Off,
            ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _strip.AddThemeFontSizeOverride("normal_font_size", 12);
        _strip.AddThemeFontSizeOverride("bold_font_size", 12);
        column.AddChild(_strip);
        _hint = new Label { HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = Control.MouseFilterEnum.Ignore };
        _hint.AddThemeFontSizeOverride("font_size", 10);
        _hint.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.68f));
        column.AddChild(_hint);

        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddChild(actions);
        var ally = new Button { Text = "Ally request (F6)", FocusMode = Control.FocusModeEnum.None, TooltipText = "A random territory on screen asks you to ally" };
        ally.AddThemeFontSizeOverride("font_size", 11);
        ally.Pressed += () => ForceAllianceRequest?.Invoke();
        actions.AddChild(ally);
        Refresh();
    }

    /// <summary>Applies every setting (after a map loads, which resets the map's material).</summary>
    public void ApplyAll()
    {
        if (_view is null)
            return;
        for (int i = 0; i < _settings.Count; i++)
            _settings[i].Apply(_values[i]);
    }

    /// <summary>Testing aid (--dev=PSC=2,SMO=1,...): sets values by abbreviation for this run, without saving.</summary>
    public void Override(string spec)
    {
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = part.Split('=');
            int i = _settings.FindIndex(x => x.Abbr.Equals(kv[0], StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && kv.Length == 2 && int.TryParse(kv[1], out int v))
                _values[i] = Math.Clamp(v, 0, _settings[i].Labels.Length - 1);
        }
        Refresh();
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key)
            return;
        switch (key.Keycode)
        {
            // (Dev keys stay off OpenFront's game shortcuts: F6/F7, and Ctrl with the arrows.)
            case Key.F6 when !key.Echo:
                ForceAllianceRequest?.Invoke();
                GetViewport().SetInputAsHandled();
                return;
            case Key.F7 when !key.Echo:
                Toggle("PLY");
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !key.CtrlPressed:
                return; // plain arrows pan the camera
            case Key.Left:
                _selected = (_selected + _settings.Count - 1) % _settings.Count;
                break;
            case Key.Right:
                _selected = (_selected + 1) % _settings.Count;
                break;
            case Key.Up:
            case Key.Down:
                var s = _settings[_selected];
                int step = key.Keycode == Key.Up ? 1 : -1;
                _values[_selected] = Math.Clamp(_values[_selected] + step, 0, s.Labels.Length - 1);
                if (_view is not null)
                    s.Apply(_values[_selected]);
                Save();
                break;
            default:
                return;
        }
        Refresh();
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Flips an on/off setting (e.g. "GRD", the coordinate grid: OpenFront's M key).</summary>
    public void Toggle(string abbr)
    {
        int i = _settings.FindIndex(x => x.Abbr == abbr);
        if (i < 0)
            return;
        _values[i] = _values[i] == 0 ? 1 : 0;
        _settings[i].Apply(_values[i]);
        Save();
        Refresh();
    }

    private void Refresh()
    {
        if (_strip is null)
            return;
        var parts = new List<string>();
        for (int i = 0; i < _settings.Count; i++)
        {
            string text = $"{_settings[i].Abbr} {_settings[i].Labels[_values[i]]}";
            parts.Add(i == _selected ? $"[color=#ffd866][b]{text}[/b][/color]" : $"[color=#b9c2b0]{text}[/color]");
        }
        _strip.Text = string.Join("[color=#5d655a]  |  [/color]", parts);
        var sel = _settings[_selected];
        _hint.Text = $"{sel.Name}: {sel.Labels[_values[_selected]]}   (Ctrl+←/→ pick, Ctrl+↑/↓ change, F7 players)";
    }

    private void Load()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) != Error.Ok)
            return;
        for (int i = 0; i < _settings.Count; i++)
            _values[i] = Math.Clamp((int)config.GetValue(ConfigSection, _settings[i].Abbr, _values[i]), 0, _settings[i].Labels.Length - 1);
    }

    private void Save()
    {
        var config = new ConfigFile();
        for (int i = 0; i < _settings.Count; i++)
            config.SetValue(ConfigSection, _settings[i].Abbr, _values[i]);
        config.Save(ConfigPath);
    }
}
