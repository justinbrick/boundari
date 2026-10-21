using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Boundari.View;

/// <summary>
/// A quick reminder of what you're playing, faded in and out as a match starts: the map's
/// name and the game's version. The user's Startup_VBox in boundari.tscn is the template
/// (fonts, sizes, shadow colours and offsets); its MapName label gets the map's name.
/// Godot only draws crisp label shadows, so each label's shadow is drawn here instead: the
/// text rendered into a SubViewport and blurred by soft_shadow.gdshader. How far it blurs is
/// the label's Shadow Outline Size when that's above 1, otherwise its shadow offset.
/// </summary>
public partial class StartupSplash : CanvasLayer
{
    private const double FadeInSeconds = 0.35;
    private const double HoldSeconds = 1.6;
    private const double FadeOutSeconds = 0.8;
    private const double MaxStep = 0.05; // a slow frame (the map loading) doesn't skip the fade
    private const string ShadowShader = "res://game/view/soft_shadow.gdshader";

    private sealed record Shadow(Label Label, SubViewport Viewport, Label Copy, TextureRect Picture, Vector2 Offset, int Pad);

    private readonly Control _root = new() { Name = "Root", AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Control _shadowLayer = new() { Name = "Shadows", AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly List<Shadow> _shadows = new();
    private Control? _box;
    private double _time = -1; // seconds since Play, or -1 when not showing

    public StartupSplash()
    {
        Name = "StartupSplash";
        Layer = 90; // over the HUD, under the dev start prompt
        Visible = false;
    }

    /// <summary>Builds the splash from the scene's Startup_VBox (hidden in the scene from now on).</summary>
    public void Build(Node scene)
    {
        AddChild(_root);
        _root.AddChild(_shadowLayer);

        var template = scene.FindChild("Startup_VBox", true, false) as Control;
        var box = template?.Duplicate() as Control ?? DefaultBox();
        if (template is not null)
            template.Visible = false; // only the copy here shows, centred on the screen
        box.Visible = true;
        _root.AddChild(box);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
        box.GrowHorizontal = Control.GrowDirection.Both;
        box.GrowVertical = Control.GrowDirection.Both;
        _box = box;

        var shader = GD.Load<Shader>(ShadowShader);
        foreach (var node in box.FindChildren("*", "Control", true, false).Prepend(box))
            if (node is Control control)
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var label in box.FindChildren("*", "Label", true, false).OfType<Label>())
            AddShadow(label, shader);
    }

    /// <summary>The name shown on the MapName label.</summary>
    public void SetMapName(string name)
    {
        if (_box?.FindChild("MapName", true, false) is Label label)
            label.Text = name;
        foreach (var shadow in _shadows)
            shadow.Copy.Text = shadow.Label.Text;
    }

    /// <summary>Fades the splash in, holds it a moment, and fades it out.</summary>
    public void Play()
    {
        _time = 0;
        _root.Modulate = new Color(1, 1, 1, 0);
        Visible = true;
        foreach (var shadow in _shadows)
            shadow.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
    }

    public override void _Process(double delta)
    {
        if (_time < 0)
            return;
        _time += Math.Min(delta, MaxStep);
        double t = _time;
        if (t >= FadeInSeconds + HoldSeconds + FadeOutSeconds)
        {
            _time = -1;
            Visible = false;
            foreach (var shadow in _shadows)
                shadow.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }
        double alpha = t < FadeInSeconds ? t / FadeInSeconds
            : t < FadeInSeconds + HoldSeconds ? 1
            : 1 - (t - FadeInSeconds - HoldSeconds) / FadeOutSeconds;
        alpha = alpha * alpha * (3 - 2 * alpha); // ease in and out
        _root.Modulate = new Color(1, 1, 1, (float)alpha);
        PlaceShadows();
    }

    /// <summary>
    /// Takes over a label's shadow: the label stops drawing its own, and a blurred copy of
    /// its text is drawn behind it instead.
    /// </summary>
    private void AddShadow(Label label, Shader shader)
    {
        var color = label.GetThemeColor("font_shadow_color");
        if (color.A <= 0)
            return;
        var offset = new Vector2(label.GetThemeConstant("shadow_offset_x"), label.GetThemeConstant("shadow_offset_y"));
        int outline = label.GetThemeConstant("shadow_outline_size");
        float blur = outline > 1 ? outline : Math.Max(Math.Max(Math.Abs(offset.X), Math.Abs(offset.Y)), 4);
        label.AddThemeColorOverride("font_shadow_color", Colors.Transparent);

        // The text's shape, in white on a transparent background (the shader only uses its alpha).
        var copy = (Label)label.Duplicate();
        copy.AddThemeColorOverride("font_color", Colors.White);
        copy.AddThemeColorOverride("font_outline_color", Colors.White);
        copy.Modulate = Colors.White;
        copy.SelfModulate = Colors.White;
        var viewport = new SubViewport
        {
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        };
        viewport.AddChild(copy);
        AddChild(viewport);

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("shadow_color", color);
        material.SetShaderParameter("blur_px", blur);
        var picture = new TextureRect
        {
            Texture = viewport.GetTexture(),
            Material = material,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _shadowLayer.AddChild(picture);
        _shadows.Add(new Shadow(label, viewport, copy, picture, offset, (int)Math.Ceiling(blur) + 2));
    }

    /// <summary>Keeps each shadow under its label (sizes settle once the box has laid out).</summary>
    private void PlaceShadows()
    {
        foreach (var s in _shadows)
        {
            var size = s.Label.Size;
            var viewportSize = new Vector2I((int)Math.Ceiling(size.X) + 2 * s.Pad, (int)Math.Ceiling(size.Y) + 2 * s.Pad);
            if (s.Viewport.Size != viewportSize)
                s.Viewport.Size = viewportSize;
            s.Copy.Position = new Vector2(s.Pad, s.Pad);
            s.Copy.Size = size;
            s.Picture.Visible = s.Label.IsVisibleInTree();
            s.Picture.Position = s.Label.GlobalPosition - _shadowLayer.GlobalPosition + s.Offset - new Vector2(s.Pad, s.Pad);
            s.Picture.Size = viewportSize;
        }
    }

    /// <summary>Used when the scene has no Startup_VBox.</summary>
    private static Control DefaultBox()
    {
        var box = new VBoxContainer { Name = "Startup_VBox" };
        box.AddThemeConstantOverride("separation", 0);
        foreach (var (name, text, size, shadow) in new[] { ("MapName", "", 160, 8), ("AlphaText", "Boundari", 48, 4) })
        {
            var label = new Label { Name = name, Text = text, HorizontalAlignment = HorizontalAlignment.Center };
            label.AddThemeFontSizeOverride("font_size", size);
            label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
            label.AddThemeConstantOverride("shadow_offset_x", shadow);
            label.AddThemeConstantOverride("shadow_offset_y", shadow);
            box.AddChild(label);
        }
        return box;
    }
}
