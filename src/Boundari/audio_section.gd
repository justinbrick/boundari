extends Button

@export var target_path: NodePath
@export var arrow_path: NodePath = "Arrow"
@export var expanded_by_default: bool = false   # <-- NEW

@onready var underline: ColorRect = $Underline
@onready var arrow: TextureRect = get_node_or_null(arrow_path)

var expanded: bool = false

func _ready() -> void:
	# Make button look like plain text
	add_theme_stylebox_override("normal", null)
	add_theme_stylebox_override("hover", null)
	add_theme_stylebox_override("pressed", null)
	add_theme_stylebox_override("focus", null)

	mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND

	pressed.connect(_toggle)
	mouse_entered.connect(_on_hover_enter)
	mouse_exited.connect(_on_hover_exit)

	underline.visible = false

	var tgt = get_node_or_null(target_path)

	if expanded_by_default:
		# Start expanded
		expanded = true
		if tgt:
			tgt.visible = true
			tgt.custom_minimum_size.y = -1  # allow natural size
		if arrow:
			arrow.rotation_degrees = 90
	else:
		# Start collapsed
		expanded = false
		if tgt:
			tgt.visible = false
			tgt.custom_minimum_size.y = 0
		if arrow:
			arrow.rotation_degrees = 0


func _toggle() -> void:
	var tgt = get_node_or_null(target_path)
	if tgt == null:
		return

	expanded = !expanded
	tgt.visible = expanded

	# Change arrow direction
	if arrow:
		arrow.rotation_degrees = 90 if expanded else 0


func _on_hover_enter() -> void:
	underline.visible = true


func _on_hover_exit() -> void:
	underline.visible = false