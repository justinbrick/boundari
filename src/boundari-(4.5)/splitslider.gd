extends Control

signal value_changed(new_value: float)

@export var min_value: float = 0.0
@export var max_value: float = 1.0
@export var value: float = 0.5

# Drag these in from the scene tree in the Inspector
@export var hbox_path: NodePath
@export var left_panel_path: NodePath
@export var right_panel_path: NodePath

var hbox: HBoxContainer
var left_panel: Panel
var right_panel: Panel


func _ready() -> void:
	# Resolve nodes from the exported paths
	if hbox_path != NodePath(""):
		hbox = get_node(hbox_path) as HBoxContainer
	if left_panel_path != NodePath(""):
		left_panel = get_node(left_panel_path) as Panel
	if right_panel_path != NodePath(""):
		right_panel = get_node(right_panel_path) as Panel

	# Sanity check to avoid null crashes
	if hbox == null or left_panel == null or right_panel == null:
		push_error("SplitSlider: One or more node paths are not set correctly.")
		return

	# Make sure this control gets mouse input
	mouse_filter = Control.MOUSE_FILTER_STOP

	# Let the root handle the mouse, children ignore it
	hbox.mouse_filter = Control.MOUSE_FILTER_IGNORE
	left_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	right_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE

	_update_panels()


func _update_panels() -> void:
	if hbox == null or left_panel == null or right_panel == null:
		return

	# Clamp and normalize value into t in [0, 1]
	var t: float = 0.0
	if max_value != min_value:
		t = clamp((value - min_value) / (max_value - min_value), 0.0, 1.0)

	# Avoid zero ratios so rounded ends never fully disappear
	var left_ratio: float = max(t, 0.001)
	var right_ratio: float = max(1.0 - t, 0.001)

	left_panel.size_flags_stretch_ratio = left_ratio
	right_panel.size_flags_stretch_ratio = right_ratio

	hbox.queue_sort()


func set_value(new_value: float) -> void:
	var clamped: float = clamp(new_value, min_value, max_value)
	if not is_equal_approx(clamped, value):
		value = clamped
		_update_panels()
		emit_signal("value_changed", value)


func _gui_input(event: InputEvent) -> void:
	if hbox == null:
		return

	if event is InputEventMouseButton:
		var mb := event as InputEventMouseButton
		if mb.button_index == MOUSE_BUTTON_LEFT and mb.pressed:
			_set_value_from_pos(mb.position.x)
	elif event is InputEventMouseMotion:
		var mm := event as InputEventMouseMotion
		if mm.button_mask & MOUSE_BUTTON_MASK_LEFT != 0:
			_set_value_from_pos(mm.position.x)


func _set_value_from_pos(local_x: float) -> void:
	var w: float = max(size.x, 0.001)
	var t: float = clamp(local_x / w, 0.0, 1.0)
	var new_value: float = lerp(min_value, max_value, t)
	set_value(new_value)
