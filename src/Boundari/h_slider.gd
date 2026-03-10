extends HSlider

var dragging = false
var default_style: StyleBox

func _ready():
    default_style = get_theme_stylebox("grabber_area")

func _gui_input(event: InputEvent) -> void:
    if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
        dragging = event.pressed
        if dragging:
            add_theme_stylebox_override("grabber_area", get_theme_stylebox("grabber_area_highlight"))
        else:
            add_theme_stylebox_override("grabber_area", default_style)

func _process(_delta: float) -> void:
    if dragging:
        if not Input.is_mouse_button_pressed(MOUSE_BUTTON_LEFT):
            dragging = false
            add_theme_stylebox_override("grabber_area", default_style)
            return
        var ratio = clamp(get_local_mouse_position().x / size.x, 0.0, 1.0)
        value = min_value + ratio * (max_value - min_value)