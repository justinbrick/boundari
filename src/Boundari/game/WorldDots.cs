using Boundari.Sim;
using Godot;

namespace Boundari;

/// <summary>
/// Attacking with dots: hovering someone else's land (or the wilderness) rings your dot
/// nearest to it that faces that land; a left click sends it, and its whole stretch of
/// border pushes in until stopped. Clicking there again while it pushes (red ring) stops
/// it, and the line it holds digs in. (The G key still sends an old-style attack.)
/// </summary>
public partial class World
{
    private Game? _hardenedGame;   // the game and version of the hardened tiles on the map
    private int _hardenedVersion;

    /// <summary>The dot a click on this tile would send or stop (null = none), and whether it would stop.</summary>
    private Dot? DotForClick(int tile, out bool stops)
    {
        stops = false;
        if (_game.Phase != GamePhase.Running || !_me.IsAlive || !_me.HasSpawned || tile < 0 || !_map.IsLand(tile))
            return null;
        var dot = _game.DotToPush(_me, tile);
        stops = dot is not null && _game.IsPushingInto(dot, _game.Owner[tile]);
        return dot;
    }

    /// <summary>A left click on land that isn't yours: sends (or stops) the dot ringed there. False if no dot answers.</summary>
    private bool ClickToPush(int tile)
    {
        if (DotForClick(tile, out bool stops) is not { } dot)
            return false;
        _game.Submit(stops ? new StopPushIntent(_me.Id, dot.Id) : new PushIntent(_me.Id, dot.Id, tile));
        return true;
    }

    /// <summary>Rings the dot a click under the mouse would send or stop (nothing while placing, aiming or over the HUD).</summary>
    private void UpdateDotHover()
    {
        Dot? dot = null;
        bool stops = false;
        bool overHud = GetViewport().GuiGetHoveredControl() is { MouseFilter: not Control.MouseFilterEnum.Ignore } over && !_panels.Contains(over);
        if (_placing is null && _aiming is null && !_startPrompt.Visible && !_fastForwarding && !_hud.AdjustingRatio && (!overHud || _args.Hover is not null))
            dot = DotForClick(_view.TileAt(HoverMouse()), out stops);
        _overlay.SetDotHover(dot?.Id ?? -1, stops);
    }

    /// <summary>Redraws the checkered borders of dug-in dots when they change.</summary>
    private void ShowHardened()
    {
        if (_hardenedGame == _game && _hardenedVersion == _game.HardenedVersion)
            return;
        _hardenedGame = _game;
        _hardenedVersion = _game.HardenedVersion;
        _view.SetHardened(_game.HardenedTiles);
    }
}
