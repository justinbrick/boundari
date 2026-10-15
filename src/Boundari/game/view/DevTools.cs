using System;
using System.Collections.Generic;
using Godot;

namespace Boundari.View;

/// <summary>
/// Visual dev tools: a compact strip in the top-right corner. Left/Right picks a setting,
/// Up/Down changes it. Choices are saved (user://devtools.cfg) and come back next run.
///   PSC patch scale        S (the map's own detail) / N (merged 2x2) / L (merged 4x4)
///   PMR patch morphing     1 (normal) .. 6: how far land-cover patches blend together
///   PSH patch shape        0 (square cells) .. 100 (smooth, the normal look)
///   OPA territory opacity  20 .. 100%
///   BRD border thickness   0 .. .8 (thin line), then 1.5 / 2.5 / 4 / 6 tiles
///   INR inner effect       off / glow / darken, along the inside of territory borders
///   GRD coordinate grid    off / 10 / 5 / 2 degrees
///   TOP topography         contour lines: off / every 100 / 250 / 500 m
///   PLY players            on / off: hide every territory, label, building and ship to see the bare map (also T)
/// </summary>
public partial class DevTools : CanvasLayer
{
    private sealed record Setting(string Abbr, string Name, string[] Labels, int Default, Action<int> Apply);

    private const string ConfigPath = "user://devtools.cfg";

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

    private MapView V => _view ?? throw new InvalidOperationException("DevTools.View isn't set");

    public DevTools()
    {
        Layer = 90;
        Name = "DevTools";
        _settings = new List<Setting>
        {
            new("PSC", "Patch scale", new[] { "S", "N", "L" }, 0, i => V.SetPatchScale(i)),
            new("PMR", "Patch morphing", new[] { "1", "2", "3", "4", "5", "6" }, 0,
                i => V.PatchMorph = i == 0 ? 0f : 0.6f + 0.2f * i), // 1 = normal blend; then wider
            new("PSH", "Patch shape (corner rounding)", new[] { "0", "20", "40", "60", "80", "100" }, 5, i => V.PatchShape = i / 5f),
            new("OPA", "Territory opacity", new[] { "20", "35", "50", "65", "80", "100" }, 2,
                i => V.TerritoryOpacity = new[] { 0.2f, 0.35f, 0.5f, 0.65f, 0.8f, 1f }[i]),
            new("BRD", "Territory border thickness (tiles from 1.5 up)", new[] { "0", ".15", ".3", ".5", ".8", "1.5", "2.5", "4", "6" }, 2,
                i => V.BorderWidth = new[] { 0f, 0.15f, 0.3f, 0.5f, 0.8f, 1.5f, 2.5f, 4f, 6f }[i]),
            new("INR", "Inner territory effect", new[] { "OFF", "GLW", "DRK" }, 0, i => V.InnerEffect = i),
            new("GRD", "Coordinate grid (degrees)", new[] { "OFF", "10", "5", "2" }, 0,
                i => V.GridStep = new[] { 0f, 10f, 5f, 2f }[i]),
            new("TOP", "Topography (contour lines, metres)", new[] { "OFF", "100", "250", "500" }, 0,
                i => V.ContourInterval = new[] { 0f, 100f, 250f, 500f }[i]),
            new("PLY", "Players, tribes and nations (T toggles)", new[] { "ON", "OFF" }, 0, i => ShowPlayers?.Invoke(i == 0)),
        };
        _values = new int[_settings.Count];
        for (int i = 0; i < _settings.Count; i++)
            _values[i] = _settings[i].Default;
        Load();
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

    /// <summary>Testing aid (--dev=PSC=2,INR=1,...): sets values by abbreviation for this run, without saving.</summary>
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
            case Key.T when !key.Echo:
                int ply = _settings.FindIndex(x => x.Abbr == "PLY");
                _values[ply] = 1 - _values[ply];
                _settings[ply].Apply(_values[ply]);
                Save();
                break;
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
        _hint.Text = $"{sel.Name}: {sel.Labels[_values[_selected]]}   (←/→ pick, ↑/↓ change, T players)";
    }

    private void Load()
    {
        var config = new ConfigFile();
        if (config.Load(ConfigPath) != Error.Ok)
            return;
        for (int i = 0; i < _settings.Count; i++)
            _values[i] = Math.Clamp((int)config.GetValue("dev", _settings[i].Abbr, _values[i]), 0, _settings[i].Labels.Length - 1);
    }

    private void Save()
    {
        var config = new ConfigFile();
        for (int i = 0; i < _settings.Count; i++)
            config.SetValue("dev", _settings[i].Abbr, _values[i]);
        config.Save(ConfigPath);
    }
}
