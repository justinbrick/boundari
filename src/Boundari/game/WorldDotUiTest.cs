using System;
using System.Linq;
using System.Threading.Tasks;
using Boundari.Sim;
using Godot;

namespace Boundari;

/// <summary>
/// Testing aid (--dot-ui=SCENARIO, with --autoplay --expand=N and --screenshot): your dots
/// gather on one stretch of border, then real mouse events are fed through Godot's input
/// (as if from the mouse) to try a dot control; the screenshot shows the result, and what
/// happened is printed. Scenarios: hover (a group's handles and a dot's tip), drag (a dot
/// mid-drag), move (dragged and let go), box / select (Ctrl+drag, mid-drag / let go),
/// spread / spread-done (an end handle mid-drag / let go), cramp-done (an end handle pulled
/// in to half the group's length and let go: a second row forms), slide-done (the middle bar,
/// let go), sell / sold (right button held a moment / long enough), radial (a quick right
/// click on a dot), buy / buy-click (buy mode over the border / clicked), upgrade (a click).
/// </summary>
public partial class World
{
    /// <summary>Before the camera frames the map: your dots gather on the border tile nearest your first dot, and stand.</summary>
    private void PrepareDotUi()
    {
        var mine = _game.DotsOf(_me);
        if (mine.Count == 0 || _game.Phase != GamePhase.Running)
            return;
        int border = _game.BorderTileNear(_me, mine[0].Tile);
        _game.Submit(new MoveDotsIntent(_me.Id, mine.Select(d => d.Id).ToArray(), border));
        for (int t = 0; t < 4000; t++)
        {
            _game.Step();
            if (t > 5 && _game.DotsOf(_me).All(d => !d.IsWalking))
                break;
        }
        GD.Print($"dot-ui: {_game.DotsOf(_me).Count} dots gathered, {_game.DotGroup(_game.DotsOf(_me)[0]).Count} in the first one's group");
    }

    private async Task RunDotUiScript(string scenario)
    {
        async Task Wait(double seconds)
        {
            ulong until = Time.GetTicksMsec() + (ulong)(seconds * 1000);
            do
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            while (Time.GetTicksMsec() < until);
        }
        Vector2 Screen(Vector2 map) => GetViewport().GetCanvasTransform() * map;
        void Move(Vector2 at, bool left = false, bool ctrl = false) =>
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = left ? MouseButtonMask.Left : 0, CtrlPressed = ctrl });
        void Button(Vector2 at, MouseButton button, bool pressed, bool ctrl = false) =>
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = button, Pressed = pressed, CtrlPressed = ctrl });
        async Task DragTo(Vector2 from, Vector2 to, bool ctrl = false)
        {
            for (int i = 1; i <= 10; i++)
            {
                Move(from.Lerp(to, i / 10f), left: true, ctrl);
                await Wait(0.02);
            }
        }

        await Wait(0.3);
        SyncSim();
        var dots = _game.DotsOf(_me).Where(d => !d.IsWalking).ToList();
        if (dots.Count == 0)
        {
            GD.Print("dot-ui: no standing dots");
            return;
        }
        var group = EndToEnd(_game.DotGroup(dots[0]));
        var middle = group[group.Count / 2];
        var dotAt = Screen(_overlay.DotDrawnAt(middle));
        var cameraWas = _camera.Position;
        // Another stretch of your border, about ten dot spacings from the group.
        int w = _map.Width, far = 10 * _game.Rules.DotSpacing;
        int there = _me.Border.Where(t => Math.Max(Math.Abs(t % w - middle.Tile % w), Math.Abs(t / w - middle.Tile / w)) is var d && d >= far - 3 && d <= far + 3)
            .DefaultIfEmpty(-1).Min();
        var thereAt = there >= 0 ? Screen(TileMiddle(there)) : dotAt + new Vector2(150, 0);

        switch (scenario)
        {
            case "hover":
                Move(dotAt);
                await Wait(0.8);
                break;
            case "drag" or "move":
                Move(dotAt);
                await Wait(0.1);
                Button(dotAt, MouseButton.Left, true);
                await DragTo(dotAt, thereAt);
                await Wait(0.2);
                if (scenario == "move")
                {
                    Button(thereAt, MouseButton.Left, false);
                    await Wait(1.5);
                }
                break;
            case "box" or "select":
                var corners = group.Select(d => Screen(_overlay.DotDrawnAt(d))).ToList();
                var topLeft = new Vector2(corners.Min(c => c.X), corners.Min(c => c.Y)) - new Vector2(30, 30);
                var bottomRight = new Vector2(corners.Max(c => c.X), corners.Max(c => c.Y)) + new Vector2(30, 30);
                Move(topLeft);
                await Wait(0.1);
                Button(topLeft, MouseButton.Left, true, ctrl: true);
                await DragTo(topLeft, bottomRight, ctrl: true);
                if (scenario == "select")
                {
                    Button(bottomRight, MouseButton.Left, false, ctrl: true);
                    Move(bottomRight + new Vector2(200, 200));
                }
                await Wait(0.3);
                break;
            case "spread" or "spread-done" or "slide-done" or "cramp-done":
                Move(dotAt);
                await Wait(0.3);
                if (_handles is null)
                {
                    GD.Print("dot-ui: no handles came up");
                    break;
                }
                int which = scenario == "slide-done" ? 2 : 1;
                var handleAt = Screen(_handles[which]);
                var outward = Screen(_handles[1]) - Screen(_handles[0]); // along the group, past its far end
                var pullTo = scenario == "cramp-done"
                    ? Screen(_handles[0]) + outward * 0.5f // the far end pulled in: the group on half its length
                    : handleAt + outward.Normalized() * (scenario == "slide-done" ? 120 : 160);
                Move(handleAt);
                await Wait(0.1);
                Button(handleAt, MouseButton.Left, true);
                await DragTo(handleAt, pullTo);
                await Wait(0.2);
                if (scenario != "spread")
                {
                    Button(pullTo, MouseButton.Left, false);
                    Move(pullTo + new Vector2(0, 300));
                    await Wait(scenario == "cramp-done" ? 8 : 4);
                }
                break;
            case "sell" or "sold" or "radial":
                Move(dotAt);
                await Wait(0.1);
                ulong pressed = Time.GetTicksMsec();
                Button(dotAt, MouseButton.Right, true);
                await Wait(scenario == "sell" ? 0.15 : scenario == "sold" ? 0.8 : 0.1);
                GD.Print($"dot-ui: right button held {Time.GetTicksMsec() - pressed} ms");
                if (scenario == "sell" && _args.Screenshot is { } shot) // the ring part filled (the screenshot proper comes once it has sold)
                    GetViewport().GetTexture().GetImage().SavePng(shot.Replace(".png", "-held.png"));
                if (scenario != "sell") // (sell: still held when the picture is taken, the ring part filled)
                {
                    Button(dotAt, MouseButton.Right, false);
                    await Wait(0.3);
                }
                break;
            case "buy" or "buy-click":
                SyncSim();
                foreach (var d in _game.DotsOf(_me).Take(4))
                    _game.Submit(new SellDotIntent(_me.Id, d.Id));
                await Wait(0.3);
                ToggleBuying();
                Move(thereAt);
                await Wait(0.5);
                if (scenario == "buy-click")
                {
                    Button(thereAt, MouseButton.Left, true);
                    Button(thereAt, MouseButton.Left, false);
                    await Wait(1.5);
                }
                break;
            case "upgrade":
                Move(dotAt);
                await Wait(0.1);
                for (int i = 0; i < 2; i++)
                {
                    Button(dotAt, MouseButton.Left, true);
                    Button(dotAt, MouseButton.Left, false);
                    await Wait(0.3);
                }
                break;
        }
        SyncSim();
        var now = _game.DotsOf(_me);
        GD.Print($"dot-ui {scenario}: camera moved {(_camera.Position - cameraWas).Length():F1}, gesture {_gesture}, buying {_buying}, "
            + $"selected {_selected.Count}, dots {now.Count} ({now.Count(d => d.IsWalking)} walking), middle dot level {_game.DotById(middle.Id)?.Strength}, "
            + $"group {(_group?.Count ?? 0)}, handles {(_handles is null ? "none" : "up")}, radial open {RadialMenuOpen()}");
    }
}
