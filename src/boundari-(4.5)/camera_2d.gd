extends Camera2D

@export var pan_speed: float = 400.0
@export var zoom_speed: float = 0.1
@export var min_zoom: float = 0.5
@export var max_zoom: float = 3.0

var dragging: bool = false
var drag_start_mouse: Vector2 = Vector2.ZERO
var drag_start_camera: Vector2 = Vector2.ZERO

var cursor_open: Texture2D
var cursor_grab: Texture2D

func _ready() -> void:
	cursor_open = load("res://cursors/cursor.png")
	cursor_grab = load("res://cursors/grab.png")
	Input.set_custom_mouse_cursor(cursor_open)

func _process(delta: float) -> void:
	_handle_wasd(delta)

func _handle_wasd(delta: float) -> void:
	var direction := Vector2.ZERO
	if Input.is_key_pressed(KEY_W): direction.y -= 1
	if Input.is_key_pressed(KEY_S): direction.y += 1
	if Input.is_key_pressed(KEY_A): direction.x -= 1
	if Input.is_key_pressed(KEY_D): direction.x += 1
	position += direction.normalized() * pan_speed * delta * (1.0 / zoom.x)

func _input(event: InputEvent) -> void:
	if get_viewport().gui_get_hovered_control() != null:
		return
	
	# --- Click and drag ---
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_LEFT:
			if event.pressed:
				dragging = true
				drag_start_mouse = get_viewport().get_mouse_position()
				drag_start_camera = position
			else:
				dragging = false
				Input.set_custom_mouse_cursor(cursor_open)

	if event is InputEventMouseMotion and dragging:
		if Input.get_mouse_button_mask() & MOUSE_BUTTON_MASK_LEFT:
			Input.set_custom_mouse_cursor(cursor_grab)
		var delta_mouse := get_viewport().get_mouse_position() - drag_start_mouse
		position = drag_start_camera - delta_mouse * (1.0 / zoom.x)

	# --- Zoom toward mouse ---
	if event is InputEventMouseButton:
		if event.button_index == MOUSE_BUTTON_WHEEL_UP or event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			var zoom_dir := 1 if event.button_index == MOUSE_BUTTON_WHEEL_UP else -1
			var old_zoom: float = zoom.x
			var new_zoom: float = clamp(old_zoom + zoom_dir * zoom_speed * old_zoom, min_zoom, max_zoom)

			var viewport_size := get_viewport_rect().size
			var mouse_pos := get_viewport().get_mouse_position()
			var mouse_offset := (mouse_pos - viewport_size / 2.0) / old_zoom

			position += mouse_offset * (1.0 - old_zoom / new_zoom)
			zoom = Vector2(new_zoom, new_zoom)