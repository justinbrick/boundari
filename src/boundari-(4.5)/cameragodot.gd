extends Camera2D

@export var pan_speed: float = 600.0
@export var drag_speed: float = 1.0
@export var zoom_step: float = 1.15
@export var zoom_min: float = 0.25
@export var zoom_max: float = 5.0

var dragging := false
var drag_anchor := Vector2.ZERO


func _unhandled_input(event: InputEvent) -> void:
	# ------------------------------------------------------
	# CLICK & DRAG START / STOP
	# ------------------------------------------------------
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			if event.pressed:
				dragging = true
				drag_anchor = get_global_mouse_position()
			else:
				dragging = false

		# ------------------------------------------------------
		# MOUSE WHEEL ZOOM (Corrected Direction)
		# ------------------------------------------------------
		if event.button_index == MOUSE_BUTTON_WHEEL_UP and event.pressed:
			_zoom_towards_cursor(1.0 / zoom_step)  # zoom IN
		elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN and event.pressed:
			_zoom_towards_cursor(zoom_step)        # zoom OUT


func _process(delta: float) -> void:
	# ------------------------------------------------------
	# KEYBOARD MOVEMENT
	# ------------------------------------------------------
	var mv := Vector2.ZERO

	if Input.is_key_pressed(KEY_W) or Input.is_key_pressed(KEY_UP):
		mv.y -= 1
	if Input.is_key_pressed(KEY_S) or Input.is_key_pressed(KEY_DOWN):
		mv.y += 1
	if Input.is_key_pressed(KEY_A) or Input.is_key_pressed(KEY_LEFT):
		mv.x -= 1
	if Input.is_key_pressed(KEY_D) or Input.is_key_pressed(KEY_RIGHT):
		mv.x += 1

	if mv != Vector2.ZERO:
		global_position += mv.normalized() * pan_speed * delta


	# ------------------------------------------------------
	# CLICK & DRAG PANNING (no flicker, perfect glue)
	# ------------------------------------------------------
	if dragging:
		var mouse_now := get_global_mouse_position()
		var delta_world := (mouse_now - drag_anchor) * drag_speed
		global_position -= delta_world
		drag_anchor = get_global_mouse_position()


# ------------------------------------------------------
# ZOOM TOWARD CURSOR
# ------------------------------------------------------
func _zoom_towards_cursor(mult: float) -> void:
	var old_zoom := zoom
	var new_zoom := old_zoom * mult

	# Clamp zoom
	new_zoom.x = clamp(new_zoom.x, zoom_min, zoom_max)
	new_zoom.y = clamp(new_zoom.y, zoom_min, zoom_max)

	# Position of mouse in world BEFORE zoom
	var mouse_before := get_global_mouse_position()

	# Apply zoom
	zoom = new_zoom

	# Where the mouse ends up AFTER zoom
	var mouse_after := get_global_mouse_position()

	# Offset camera so that zoom occurs under cursor
	global_position += (mouse_before - mouse_after)