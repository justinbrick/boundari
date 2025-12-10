using System;
using Godot;

public partial class Camera : Camera2D
{
    [Export] public ColorRect Bounds { get; set; } = null!;
    [Export] public float PanSpeed = 500f;
    [Export] public float DragPanSpeed = 1.0f;
    [Export] public float ZoomSpeed = 1.1f;
    [Export] public float MinZoom = 1f;
    [Export] public float MaxZoom = 100f;

    private bool _isDragging = false;
    private Vector2 _dragStartMouseWorld;
    private Vector2 _dragStartCameraPos;

    public override void _Ready()
    {
        if (Bounds is null)
            throw new InvalidOperationException("Bounds property must be set.");
    }

    public override void _Input(InputEvent e)
    {
        // ---------------------------------------------------
        // LEFT-MOUSE WORLD-LOCKED DRAGGING (correct version)
        // ---------------------------------------------------
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _isDragging = true;
                    _dragStartMouseWorld = GetViewport().GetCamera2D().GetGlobalMousePosition();
                    _dragStartCameraPos = GlobalPosition;
                }
                else
                {
                    _isDragging = false;
                }
            }
        }

        // ---------------------------------------------------
        // ZOOMING TOWARD MOUSE CURSOR
        // ---------------------------------------------------
        if (e.IsActionPressed("zoom_in"))
            ZoomTowardsMouse(ZoomSpeed);       // zoom in (wheel up)

        if (e.IsActionPressed("zoom_out"))
            ZoomTowardsMouse(1f / ZoomSpeed);  // zoom out (wheel down)
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // ---------------------------------------------------
        // KEYBOARD PANNING (WASD / ARROWS)
        // ---------------------------------------------------
        var movement = Vector2.Zero;
        if (Input.IsActionPressed("pan_up")) movement.Y -= 1;
        if (Input.IsActionPressed("pan_down")) movement.Y += 1;
        if (Input.IsActionPressed("pan_left")) movement.X -= 1;
        if (Input.IsActionPressed("pan_right")) movement.X += 1;

        if (movement != Vector2.Zero)
        {
            float adjustedSpeed = PanSpeed / Mathf.Sqrt(Zoom.X * 2);
            Position += movement.Normalized() * adjustedSpeed * dt;
        }

        // ---------------------------------------------------
        // TRUE WORLD-LOCKED DRAGGING (correct version)
        // ---------------------------------------------------
        if (_isDragging)
        {
            Vector2 currentMouseWorld =
                GetViewport().GetCamera2D().GetGlobalMousePosition();

            Vector2 deltaWorld = _dragStartMouseWorld - currentMouseWorld;
            GlobalPosition = _dragStartCameraPos + deltaWorld;
        }

        EnsureWithinBounds();
    }

    // =======================================================
    // ZOOMING THAT FOLLOWS THE MOUSE CURSOR
    // =======================================================
    private void ZoomTowardsMouse(float factor)
    {
        Vector2 before = GetViewport().GetCamera2D().GetGlobalMousePosition();

        Zoom *= factor;
        Zoom = Zoom.Clamp(MinZoom, MaxZoom);

        Vector2 after = GetViewport().GetCamera2D().GetGlobalMousePosition();
        GlobalPosition += before - after;

        EnsureWithinBounds();
    }

    // =======================================================
    // KEEP CAMERA INSIDE BOUNDS
    // =======================================================
    private void EnsureWithinBounds()
    {
        var screenSize = GetViewportRect().Size / Zoom;
        var halfScreen = screenSize / 2f;

        var bounds = Bounds.GetGlobalRect();
        var pos = GlobalPosition;

        float minX = bounds.Position.X + halfScreen.X;
        float maxX = bounds.Position.X + bounds.Size.X - halfScreen.X;
        float minY = bounds.Position.Y + halfScreen.Y;
        float maxY = bounds.Position.Y + bounds.Size.Y - halfScreen.Y;

        pos.X = Mathf.Clamp(pos.X, minX, maxX);
        pos.Y = Mathf.Clamp(pos.Y, minY, maxY);

        GlobalPosition = pos;
    }
}