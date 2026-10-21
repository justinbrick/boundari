using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The notification bar (Button_Notif in boundari.tscn): shows one message at a time from a
/// queue. Each stays up at least <see cref="MinSeconds"/> so it can be read, and at most
/// <see cref="MaxSeconds"/>; the queue's length shows in the corner badge (if the scene has one).
/// Urgent messages (attacks, bombs, betrayals) jump ahead of the others. Clicking the bar opens
/// or closes Panel_Notif, a scrollable list of the last <see cref="MaxHistory"/> messages,
/// newest at the bottom like OpenFront's events panel. Messages about something the player sent
/// end in "*".
/// </summary>
public sealed class NotificationBar
{
    private const string Path = "Main_Layer/Main_Control/Large_Rect/Notif_HBox/HBoxContainer/VBoxContainer/Button_Notif";

    private const double MinSeconds = 2.5;   // before a queued message may replace this one
    private const double MaxSeconds = 6;     // then it goes, even with nothing waiting
    private const double FadeInSeconds = 0.15;
    private const double FadeOutSeconds = 0.3;
    private const double RepeatSeconds = 10; // the same message isn't shown again this soon
    private const int MaxQueued = 20;        // beyond this the oldest ordinary messages are dropped
    private const int MaxHistory = 30;       // OpenFront's events panel keeps its last 30
    private const int ListMargin = 10;       // px between the panel's edge and the list
    private const string BlurShader = "res://blurroundcorn.gdshader";
    private static readonly Color UrgentColor = new(1f, 0.55f, 0.55f);

    private sealed record Message(string Text, bool Urgent);

    private readonly Label? _text, _idle, _count;
    private readonly CanvasItem? _badge;
    private readonly List<Message> _queue = new();
    private readonly Control? _panel;
    private readonly ScrollContainer? _scroll;
    private readonly VBoxContainer? _list;
    private bool _stickToBottom = true;
    private readonly Dictionary<string, double> _lastShown = new();
    private Message? _current;
    private double _shownFor, _clock, _fadingOut = -1;
    private bool _wasAlive;

    public NotificationBar(Node root)
    {
        var hud = root.GetNodeOrNull("Main_Layer/Main_Control");
        var bar = root.GetNodeOrNull<Control>(Path) ?? hud?.FindChild("Button_Notif", true, false) as Control;
        _text = bar?.GetNodeOrNull<Label>("Label_Notif_Example");
        _idle = bar?.GetNodeOrNull<Label>("Label_No_Notif");
        _badge = bar?.GetNodeOrNull<CanvasItem>("ColorRect");
        _count = bar?.GetNodeOrNull<Label>("ColorRect/Label_Notif_Queue");
        if (_text is not null)
        {
            // Messages use the width left of the queue badge, cut short with "..." if too long.
            _text.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _text.AnchorLeft = 0.03f;
            _text.AnchorRight = _badge is Control b ? b.AnchorLeft - 0.01f : 0.97f;
            _text.OffsetLeft = _text.OffsetRight = _text.OffsetTop = _text.OffsetBottom = 0;
            _text.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }
        if (_count is not null)
        {
            _count.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _count.OffsetLeft = _count.OffsetRight = _count.OffsetTop = _count.OffsetBottom = 0;
        }
        if (bar is not null)
        {
            bar.Visible = true;
            // The boxes around the bar are only layout: let clicks and hovers through to the map.
            for (var n = bar.GetParent() as Control; n is Container; n = n.GetParent() as Control)
                n.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
        if (bar is BaseButton button)
        {
            button.Pressed += ToggleList;
            button.FocusMode = Control.FocusModeEnum.None; // so Space/Enter keep working in the game
        }

        _panel = bar?.GetParent()?.GetNodeOrNull<Control>("Panel_Notif");
        if (_panel is null && hud?.FindChild("Panel_Notif", true, false) is Control found and not BaseButton)
            _panel = found;
        if (_panel is not null)
        {
            AddBlur(_panel);
            (_scroll, _list) = MakeList(_panel);
            _panel.Visible = false; // opens from the bar
        }
        Show(null);
    }

    /// <summary>
    /// Blurs and darkens the map behind the panel (the scene's blurroundcorn shader, set like
    /// its other blurred panels), drawn under the panel's own see-through colour and border.
    /// </summary>
    private static void AddBlur(Control panel)
    {
        if (!ResourceLoader.Exists(BlurShader) || panel.GetNodeOrNull("Blur_Notif") is not null)
            return;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>(BlurShader) };
        material.SetShaderParameter("blur_amount", 3.5f);
        material.SetShaderParameter("darken_amount", 0.81f);
        material.SetShaderParameter("noise_strength", 0.025f);
        material.SetShaderParameter("noise_speed", 0f);
        material.SetShaderParameter("corner_smooth_px", 1f);
        int radius = panel.GetThemeStylebox("panel") is StyleBoxFlat box ? box.CornerRadiusTopLeft : 6;
        material.SetShaderParameter("corner_radius_px", (float)radius);
        var blur = new ColorRect
        {
            Name = "Blur_Notif",
            Material = material,
            ShowBehindParent = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        panel.AddChild(blur);
        panel.MoveChild(blur, 0);
        blur.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // The shader rounds its corners in pixels, so it needs the panel's size.
        blur.Resized += () => material.SetShaderParameter("rect_size", blur.Size);
    }

    /// <summary>
    /// The list inside Panel_Notif: a ScrollContainer and VBoxContainer the scene already has,
    /// or new ones filling the panel.
    /// </summary>
    private (ScrollContainer, VBoxContainer) MakeList(Control panel)
    {
        var scroll = FirstChild<ScrollContainer>(panel);
        if (scroll is null)
        {
            scroll = new ScrollContainer { Name = "Scroll_Notif" };
            panel.AddChild(scroll);
            scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            scroll.OffsetLeft = scroll.OffsetTop = ListMargin;
            scroll.OffsetRight = scroll.OffsetBottom = -ListMargin;
        }
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.FocusMode = Control.FocusModeEnum.None;
        var list = FirstChild<VBoxContainer>(scroll);
        if (list is null)
        {
            list = new VBoxContainer { Name = "List_Notif" };
            list.AddThemeConstantOverride("separation", 6);
            scroll.AddChild(list);
        }
        list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // New rows keep the list scrolled to the bottom, unless the player has scrolled up to read.
        var scrollBar = scroll.GetVScrollBar();
        scrollBar.Changed += () =>
        {
            if (_stickToBottom)
                scroll.ScrollVertical = (int)scrollBar.MaxValue;
        };
        scrollBar.ValueChanged += value => _stickToBottom = scrollBar.MaxValue - scrollBar.Page - value < 5;
        return (scroll, list);
    }

    private static T? FirstChild<T>(Node parent) where T : Node
    {
        foreach (var child in parent.GetChildren())
            if (child is T match)
                return match;
        return null;
    }

    /// <summary>(Testing, --notifs) a few kinds of message, with the list open.</summary>
    public void ShowSamples(Game game, Player me)
    {
        var others = game.Players.Where(p => p != me && p.IsAlive).Take(3).Select(p => (ushort)p.Id).ToList();
        if (others.Count < 3)
            return;
        ushort i = (ushort)me.Id;
        GameEvent[] samples =
        {
            new(GameEventKind.AllianceRequested, i, others[0]),
            new(GameEventKind.AllianceRequested, others[1], i),
            new(GameEventKind.AllianceAccepted, i, others[0]),
            new(GameEventKind.QuickChat, i, others[1], 0),
            new(GameEventKind.QuickChat, others[2], i, 1),
            new(GameEventKind.EmbargoStarted, i, others[2]),
            new(GameEventKind.Targeted, i, others[2]),
            new(GameEventKind.AttackStarted, others[2], i),
            new(GameEventKind.AllianceEnding, i, others[0]),
            new(GameEventKind.AllianceBroken, others[0], i),
        };
        foreach (var e in samples)
            if (Describe(game, me, e) is var (text, urgent) && text is not null)
                Push(text, urgent);
        ToggleList();
    }

    /// <summary>A click on the bar: opens or closes the list.</summary>
    private void ToggleList()
    {
        if (_panel is null)
            return;
        _panel.Visible = !_panel.Visible;
        if (_panel.Visible && _scroll is not null)
        {
            _stickToBottom = true;
            _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue;
        }
    }

    /// <summary>Adds a row to the bottom of the list, dropping the oldest past <see cref="MaxHistory"/>.</summary>
    private void AddToList(Message message)
    {
        if (_list is null)
            return;
        // Rows copy the look of the bar's message label (its font size and tint).
        var row = _text?.Duplicate() as Label ?? new Label();
        row.Visible = true;
        row.SelfModulate = Colors.White;
        row.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        row.CustomMinimumSize = Vector2.Zero;
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.HorizontalAlignment = HorizontalAlignment.Left;
        row.VerticalAlignment = VerticalAlignment.Top;
        row.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        row.MouseFilter = Control.MouseFilterEnum.Ignore;
        row.Text = message.Text;
        if (message.Urgent)
        {
            // The scene's label is tinted far brighter than white, which would wash red out: divide it back.
            var tint = row.Modulate;
            row.AddThemeColorOverride("font_color", new Color(UrgentColor.R / Math.Max(tint.R, 1), UrgentColor.G / Math.Max(tint.G, 1), UrgentColor.B / Math.Max(tint.B, 1)));
        }
        _list.AddChild(row);
        while (_list.GetChildCount() > MaxHistory)
        {
            var oldest = _list.GetChild(0);
            _list.RemoveChild(oldest);
            oldest.QueueFree();
        }
    }

    /// <summary>Adds a message to the queue (urgent ones go ahead of ordinary ones).</summary>
    public void Push(string text, bool urgent = false)
    {
        if (_current?.Text == text || _queue.Exists(m => m.Text == text))
            return;
        if (_lastShown.TryGetValue(text, out double at) && _clock - at < RepeatSeconds)
            return;
        var message = new Message(text, urgent);
        AddToList(message);
        if (urgent)
            _queue.Insert(_queue.FindIndex(m => !m.Urgent) is int i and >= 0 ? i : _queue.Count, message);
        else
            _queue.Add(message);
        while (_queue.Count > MaxQueued)
        {
            int oldest = _queue.FindIndex(m => !m.Urgent);
            _queue.RemoveAt(oldest >= 0 ? oldest : 0);
        }
    }

    /// <summary>Turns this tick's game events into messages for <paramref name="me"/>, then clears them.</summary>
    public void TakeEvents(Game game, Player me)
    {
        bool conqueredMe = false;
        foreach (var e in game.Events)
        {
            conqueredMe |= e.Kind == GameEventKind.Conquered && e.B == me.Id;
            if (Describe(game, me, e) is var (text, urgent) && text is not null)
                Push(text, urgent);
        }
        game.Events.Clear();

        // Wiped out without anyone finishing us off (e.g. walled in and annexed).
        bool alive = me.HasSpawned && me.IsAlive;
        if (_wasAlive && !alive && !conqueredMe)
            Push("You have been defeated.", urgent: true);
        _wasAlive = alive;
    }

    private static (string? Text, bool Urgent) Describe(Game game, Player me, GameEvent e)
    {
        string A = game.GetPlayer(e.A)?.Name ?? "Someone";
        string B = game.GetPlayer(e.B)?.Name ?? "someone";
        bool byMe = e.A == me.Id, toMe = e.B == me.Id;
        return e.Kind switch
        {
            GameEventKind.AllianceRequested when byMe => ($"Alliance request sent to {B}.*", false),
            GameEventKind.AllianceRequested when toMe => ($"{A} requests an alliance.", false),
            GameEventKind.AllianceAccepted when byMe => ($"{B} accepted your alliance.", false),
            GameEventKind.AllianceAccepted when toMe => ($"You are now allied with {A}.", false),
            GameEventKind.AllianceRejected when byMe => ($"{B} declined your alliance request.", false),
            GameEventKind.AllianceRequestExpired when byMe => ($"{B} didn't answer your alliance request.", false),
            GameEventKind.AllianceExpired => ($"Your alliance with {(byMe ? B : A)} has ended.", false),
            GameEventKind.AllianceEnding => ($"Your alliance with {(byMe ? B : A)} ends in 30 s. Right-click them to renew.", false),
            GameEventKind.AllianceRenewRequested when toMe => ($"{A} wants to renew your alliance.", false),
            GameEventKind.AllianceRenewRequested when byMe => ($"Asked {B} to renew your alliance.*", false),
            GameEventKind.AllianceRenewed => ($"Your alliance with {(byMe ? B : A)} is renewed.", false),
            GameEventKind.AllianceBroken when toMe => ($"{A} broke your alliance!", true),
            GameEventKind.AllianceBroken when byMe => ($"You broke your alliance with {B}.*", false),
            GameEventKind.AttackStarted when toMe => ($"{A} is attacking you!", true),
            GameEventKind.BoatLaunched when toMe => ($"{A} is sending a boat your way!", true),
            GameEventKind.NukeLaunched when toMe => ($"{A} launched {BombName((NukeType)e.Detail)} at you!", true),
            GameEventKind.EmbargoStarted when byMe => ($"You stopped trading with {B}.*", false),
            GameEventKind.EmbargoStarted when toMe => ($"{A} stopped trading with you", false),
            GameEventKind.EmbargoStopped when byMe => ($"You resumed trading with {B}.*", false),
            GameEventKind.EmbargoStopped when toMe => ($"{A} resumed trading with you", false),
            GameEventKind.Targeted when byMe => ($"You marked {B} as a target.*", false),
            GameEventKind.Targeted when toMe => ($"{A} marked you as a target!", true),
            GameEventKind.Targeted => ($"Your ally {A} marked {B} as a target.", false),
            GameEventKind.QuickChat when toMe => ($"{A}: {Phrase(e.Detail)}", false),
            GameEventKind.QuickChat when byMe => ($"To {B}: {Phrase(e.Detail)}*", false),
            GameEventKind.Conquered when byMe => ($"You conquered {B}.", false),
            GameEventKind.Conquered when toMe => ($"{A} conquered you.", true),
            _ => (null, false),
        };
    }

    private static string Phrase(long index) => index >= 0 && index < ChatRules.Phrases.Length ? ChatRules.Phrases[index] : "...";

    private static string BombName(NukeType type) => type switch
    {
        NukeType.Moab => "a Mortar",
        NukeType.FatMan => "a Missile",
        NukeType.TsarBomba => "an Atomic Bomb",
        _ => "a bomb",
    };

    /// <summary>Every frame: runs the timers and the fades.</summary>
    public void Update(double delta)
    {
        _clock += delta;
        if (_fadingOut >= 0)
        {
            // Fading out: then the next message, or the idle text.
            _fadingOut += delta;
            SetAlpha(1 - _fadingOut / FadeOutSeconds);
            if (_fadingOut >= FadeOutSeconds)
            {
                _fadingOut = -1;
                ShowNext();
            }
        }
        else if (_current is not null)
        {
            _shownFor += delta;
            SetAlpha(_shownFor / FadeInSeconds);
            if ((_queue.Count > 0 && _shownFor >= MinSeconds) || _shownFor >= MaxSeconds)
                _fadingOut = 0;
        }
        else if (_queue.Count > 0)
            ShowNext();

        GameHud.SetText(_count, _queue.Count.ToString(CultureInfo.InvariantCulture));
        if (_badge is not null && _badge.Visible != _queue.Count > 0)
            _badge.Visible = _queue.Count > 0;
    }

    private void ShowNext()
    {
        if (_queue.Count == 0)
        {
            Show(null);
            return;
        }
        var next = _queue[0];
        _queue.RemoveAt(0);
        Show(next);
    }

    private void Show(Message? message)
    {
        _current = message;
        _shownFor = 0;
        if (message is not null)
            _lastShown[message.Text] = _clock;
        if (_text is not null)
        {
            _text.Visible = message is not null;
            GameHud.SetText(_text, message?.Text ?? "");
        }
        if (_idle is not null)
            _idle.Visible = message is null;
        SetAlpha(message is null ? 1 : 0);
    }

    // Fades with self-modulate, leaving the scene's own modulate (its tint) alone.
    private void SetAlpha(double alpha)
    {
        if (_text is not null)
            _text.SelfModulate = new Color(1, 1, 1, (float)Math.Clamp(alpha, 0, 1));
    }
}
