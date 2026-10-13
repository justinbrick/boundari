extends TextureRect

signal value_changed(new_value: float)

@export var min_value: float = 0.0
@export var max_value: float = 1.0
@export var value: float = 0.5

@onready var line_indicator: ColorRect = $LineIndicator
@onready var shader_mat: ShaderMaterial = material as ShaderMaterial


func _ready() -> void:
    # Make sure this node actually receives mouse events
    mouse_filter = Control.MOUSE_FILTER_STOP

    # Whenever this control is resized, update shader + line
    resized.connect(_on_resized)

    # Also run once now (if size is already valid)
    _on_resized()


func _on_resized() -> void:
    _update_rect_size_in_shader()
    update_line_and_shader()


func _update_rect_size_in_shader() -> void:
    if shader_mat:
        shader_mat.set_shader_parameter("rect_size_px", size)


func update_line_and_shader() -> void:
    # Normalize value -> t in [0, 1]
    var t: float = 0.0
    if max_value != min_value:
        t = clamp((value - min_value) / (max_value - min_value), 0.0, 1.0)

    # --- Position the line ---
    # Line should span full height of the slider
    line_indicator.size.y = size.y

    # X position: map t (0..1) to [0 .. width], then center the line on that
    var line_w: float = line_indicator.size.x
    var usable_width: float = size.x
    var x: float = t * usable_width - line_w * 0.5

    line_indicator.position.x = x
    line_indicator.position.y = 0.0

    # --- Update the shader's line_pos uniform ---
    if shader_mat:
        shader_mat.set_shader_parameter("line_pos", t)


func _gui_input(event: InputEvent) -> void:
    if event is InputEventMouseButton:
        var mb = event as InputEventMouseButton
        if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
            _set_value_from_mouse(mb.position.x)
    elif event is InputEventMouseMotion:
        var mm = event as InputEventMouseMotion
        # Drag with left button held
        if mm.button_mask & MOUSE_BUTTON_MASK_LEFT != 0:
            _set_value_from_mouse(mm.position.x)


func _set_value_from_mouse(local_x: float) -> void:
    var width: float = max(size.x, 0.001)
    var t: float = clamp(local_x / width, 0.0, 1.0)
    var new_value: float = lerp(min_value, max_value, t)

    if not is_equal_approx(new_value, value):
        value = new_value
        update_line_and_shader()
        emit_signal("value_changed", value)