using System;
using Godot;

namespace Boundari;

/// <summary>
/// Sees input before the rest of the scene (it is added as World's last child, and Godot
/// hands input to the last nodes first), so a press that grabs a dot, a box selection or a
/// group handle never also starts the camera drag (camera_2d.gd), and the wheel in buy mode
/// never also zooms. <see cref="Handler"/> returns true to keep the event for itself.
/// </summary>
public partial class MapInputGate : Node
{
    public Func<InputEvent, bool>? Handler { get; set; }

    public override void _Input(InputEvent e)
    {
        if (Handler?.Invoke(e) == true)
            GetViewport().SetInputAsHandled();
    }
}
