using System;
using System.Collections.Generic;
using System.Globalization;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The notification bar (Panel_Notif in boundari.tscn): shows one message at a time from a
/// queue. Each stays up at least <see cref="MinSeconds"/> so it can be read, and at most
/// <see cref="MaxSeconds"/>; the queue's length shows in the corner badge. Urgent messages
/// (attacks, bombs, betrayals) jump ahead of the others. Clicking the bar skips to the next.
/// </summary>
public sealed class NotificationBar
{
    private const string Path = "Main_Layer/Main_Control/Large_Rect/HBoxContainer2/Panel_Notif";

    private const double MinSeconds = 2.5;   // before a queued message may replace this one
    private const double MaxSeconds = 6;     // then it goes, even with nothing waiting
    private const double FadeInSeconds = 0.15;
    private const double FadeOutSeconds = 0.3;
    private const double RepeatSeconds = 10; // the same message isn't shown again this soon
    private const int MaxQueued = 20;        // beyond this the oldest ordinary messages are dropped

    private sealed record Message(string Text, bool Urgent);

    private readonly Label? _text, _idle, _count;
    private readonly CanvasItem? _badge;
    private readonly List<Message> _queue = new();
    private readonly Dictionary<string, double> _lastShown = new();
    private Message? _current;
    private double _shownFor, _clock, _fadingOut = -1;
    private bool _wasAlive;

    public NotificationBar(Node root)
    {
        var panel = root.GetNodeOrNull<Control>(Path);
        _text = panel?.GetNodeOrNull<Label>("Label_Notif_Example");
        _idle = panel?.GetNodeOrNull<Label>("Label_No_Notif");
        _badge = panel?.GetNodeOrNull<CanvasItem>("ColorRect");
        _count = panel?.GetNodeOrNull<Label>("ColorRect/Label_Notif_Queue");
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
        if (panel is BaseButton button)
        {
            button.Pressed += Skip;
            button.FocusMode = Control.FocusModeEnum.None; // so Space/Enter keep working in the game
        }
        Show(null);
    }

    /// <summary>Adds a message to the queue (urgent ones go ahead of ordinary ones).</summary>
    public void Push(string text, bool urgent = false)
    {
        if (_current?.Text == text || _queue.Exists(m => m.Text == text))
            return;
        if (_lastShown.TryGetValue(text, out double at) && _clock - at < RepeatSeconds)
            return;
        var message = new Message(text, urgent);
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
            GameEventKind.AllianceRequested when byMe => ($"Alliance request sent to {B}.", false),
            GameEventKind.AllianceRequested when toMe => ($"{A} requests an alliance.", false),
            GameEventKind.AllianceAccepted when byMe => ($"{B} accepted your alliance.", false),
            GameEventKind.AllianceAccepted when toMe => ($"You are now allied with {A}.", false),
            GameEventKind.AllianceRejected when byMe => ($"{B} declined your alliance request.", false),
            GameEventKind.AllianceRequestExpired when byMe => ($"{B} didn't answer your alliance request.", false),
            GameEventKind.AllianceExpired => ($"Your alliance with {(byMe ? B : A)} has ended.", false),
            GameEventKind.AllianceEnding => ($"Your alliance with {(byMe ? B : A)} ends in 30 s. Right-click them to renew.", false),
            GameEventKind.AllianceRenewRequested when toMe => ($"{A} wants to renew your alliance.", false),
            GameEventKind.AllianceRenewRequested when byMe => ($"Asked {B} to renew your alliance.", false),
            GameEventKind.AllianceRenewed => ($"Your alliance with {(byMe ? B : A)} is renewed.", false),
            GameEventKind.AllianceBroken when toMe => ($"{A} broke your alliance!", true),
            GameEventKind.AllianceBroken when byMe => ($"You broke your alliance with {B}.", false),
            GameEventKind.AttackStarted when toMe => ($"{A} is attacking you!", true),
            GameEventKind.BoatLaunched when toMe => ($"{A} is sending a boat your way!", true),
            GameEventKind.NukeLaunched when toMe => ($"{A} launched {BombName((NukeType)e.Detail)} at you!", true),
            GameEventKind.EmbargoStarted when byMe => ($"You stopped trading with {B}", false),
            GameEventKind.EmbargoStarted when toMe => ($"{A} stopped trading with you", false),
            GameEventKind.EmbargoStopped when byMe => ($"You resumed trading with {B}", false),
            GameEventKind.EmbargoStopped when toMe => ($"{A} resumed trading with you", false),
            GameEventKind.Targeted when byMe => ($"You marked {B} as a target.", false),
            GameEventKind.Targeted when toMe => ($"{A} marked you as a target!", true),
            GameEventKind.Targeted => ($"Your ally {A} marked {B} as a target.", false),
            GameEventKind.Conquered when byMe => ($"You conquered {B}.", false),
            GameEventKind.Conquered when toMe => ($"{A} conquered you.", true),
            _ => (null, false),
        };
    }

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

    /// <summary>A click on the bar: on to the next message (or clear this one).</summary>
    private void Skip()
    {
        if (_current is not null && _fadingOut < 0)
            _fadingOut = 0;
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
