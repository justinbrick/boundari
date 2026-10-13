extends ColorRect

func _ready() -> void:
	set_process_unhandled_input(true)


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.pressed:
		# If the click is OUTSIDE this rect → hide the panel
		if not get_global_rect().has_point(event.position):
			hide()