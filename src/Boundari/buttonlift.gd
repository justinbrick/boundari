extends Button

var normal_pos: Vector2
var hover_offset := Vector2(0, -8)
var tween: Tween

func _ready():
	connect("mouse_entered", Callable(self, "_on_mouse_entered"))
	connect("mouse_exited", Callable(self, "_on_mouse_exited"))
	# Wait one frame so containers/layout can place the button first
	await get_tree().process_frame
	normal_pos = position


func _on_mouse_entered():
	if tween:
		tween.kill()
	tween = get_tree().create_tween()
	tween.tween_property(self, "position", normal_pos + hover_offset, 0.12) \
		.set_trans(Tween.TRANS_SINE) \
		.set_ease(Tween.EASE_OUT)


func _on_mouse_exited():
	if tween:
		tween.kill()
	tween = get_tree().create_tween()
	tween.tween_property(self, "position", normal_pos, 0.12) \
		.set_trans(Tween.TRANS_SINE) \
		.set_ease(Tween.EASE_OUT)
