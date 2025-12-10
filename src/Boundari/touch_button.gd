extends TouchScreenButton

@export var normal_color: Color = Color(1, 1, 1)
@export var hover_color: Color = Color(1.15, 1.15, 1.15)
@export var pressed_color: Color = Color(0.7, 0.7, 0.7)

var is_hovered: bool = false

func _ready():
	modulate = normal_color

	# TouchScreenButton DOES have these signals once a shape is assigned
	connect("mouse_entered", _on_hover)
	connect("mouse_exited", _on_unhover)
	connect("pressed", _on_pressed)
	connect("released", _on_released)


func _on_hover():
	is_hovered = true
	modulate = hover_color


func _on_unhover():
	is_hovered = false
	modulate = normal_color


func _on_pressed():
	modulate = pressed_color


func _on_released():
	# If still inside the polygon → use hover color
	if is_hovered:
		modulate = hover_color
	else:
		modulate = normal_color