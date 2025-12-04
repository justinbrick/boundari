extends Control

# 380x380 → radius 190
const MENU_RADIUS := 190.0

func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	
	# Force all wrapper nodes to ignore the mouse
	_force_ignore(self)


func _force_ignore(node: Node) -> void:
	for child in node.get_children():
		# All wrapper containers must ignore all mouse input
		if child is Control and not (child is TextureButton):
			child.mouse_filter = Control.MOUSE_FILTER_IGNORE

		# TextureButtons should listen (the wedges)
		if child is TextureButton:
			child.mouse_filter = Control.MOUSE_FILTER_PASS

		_force_ignore(child)


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:

		var center: Vector2 = global_position + (size * 0.5)
		var dist := center.distance_to(event.position)

		# Outside circle → let Main_Control close menu
		if dist > MENU_RADIUS:
			return

		# Inside → consume event so parent doesn't close menu
		accept_event()