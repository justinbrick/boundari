using System;
using Godot;


public partial class Camera : Camera2D
{
    /// <summary>
    /// The bounds to which the camera is constrained.
    /// </summary>
    [Export]
    public ColorRect Bounds { get; set; } = null!;


    public override void _Ready()
    {
        if (Bounds is null)
            throw new InvalidOperationException("Bounds property must be set.");
    }


    public override void _Input(InputEvent @event)
    {
        var zoom =
            @event.IsActionPressed("zoom_in") ? 1.1f
            : @event.IsActionPressed("zoom_out") ? 0.9f
            : 0;


        if (zoom == 0)
            return;


        ZoomCamera(zoom);
    }


    public override void _Process(double delta)
    {
        var movement = Vector2.Zero;
        if (Input.IsActionPressed("pan_up"))
            movement.Y -= 1;
        if (Input.IsActionPressed("pan_down"))
            movement.Y += 1;
        if (Input.IsActionPressed("pan_left"))
            movement.X -= 1;
        if (Input.IsActionPressed("pan_right"))
            movement.X += 1;
        Position += movement * (800 / (float)Math.Sqrt(Zoom.X * 2)) * (float)delta;
        EnsureWithinBounds();
    }


    private void ZoomCamera(float amount)
    {
        Zoom *= new Vector2(1f * amount, 1f * amount);
        Zoom = Zoom.Clamp(1f, 100f);
    }


    private void EnsureWithinBounds()
    {
        var screenSize = GetViewportRect().Size / Zoom;
        var halfScreenSize = screenSize / 2;


        var boundsRect = Bounds.GetGlobalRect();
        var cameraPos = GlobalPosition;


        var minX = boundsRect.Position.X + halfScreenSize.X;
        var maxX = boundsRect.Position.X + boundsRect.Size.X - halfScreenSize.X;
        var minY = boundsRect.Position.Y + halfScreenSize.Y;
        var maxY = boundsRect.Position.Y + boundsRect.Size.Y - halfScreenSize.Y;


        cameraPos.X = Mathf.Clamp(cameraPos.X, minX, maxX);
        cameraPos.Y = Mathf.Clamp(cameraPos.Y, minY, maxY);


        GlobalPosition = cameraPos;
    }
}
