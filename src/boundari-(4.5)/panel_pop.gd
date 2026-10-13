extends Control

@export var color_rect: ColorRect
@export var toggle_button: Button

func _ready():
	if color_rect:
		color_rect.visible = false
	if toggle_button:
		toggle_button.button_down.connect(_on_button_pressed)

func _on_button_pressed():
	if color_rect:
		color_rect.visible = !color_rect.visible

func _input(event):
	if color_rect and color_rect.visible and event is InputEventMouseButton:
		if event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
			var rect = color_rect.get_global_rect()
			if not rect.has_point(event.global_position):
				color_rect.visible = false
