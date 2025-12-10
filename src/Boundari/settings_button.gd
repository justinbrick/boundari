extends Button

@export var options_rect_path: NodePath   # drag your Options_Rect here

var options_rect: Control

func _ready() -> void:
	options_rect = get_node(options_rect_path)
	pressed.connect(_on_pressed)


func _on_pressed() -> void:
	options_rect.visible = !options_rect.visible
