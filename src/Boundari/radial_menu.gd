extends Control

# The radial menu's wedges. Every wedge picture is a full 380x380 image with the wedge
# drawn in its own part and the rest see-through, so all the buttons cover the same
# square and Godot can't tell which one the mouse is on. Instead this script looks at
# the pictures itself: the wedge under the mouse is the one whose picture isn't
# see-through there. That wedge lightens on hover and darkens while pressed.

# 380x380 → radius 190
const MENU_RADIUS := 190.0
const ALPHA_THRESHOLD := 0.02         # less opaque than this counts as "not on the wedge" (the spokes are very faint)
# How much lighter/darker a wedge gets: its frosted-glass panel (blur.gdshader's tint) and its picture.
const HOVER_TINT := 1.45
const PRESSED_TINT := 0.6

## Sent when a wedge is clicked (pressed and released on the same wedge). The name is the
## wedge's holder node, e.g. "T_Rect" or "Center_Rect".
signal wedge_pressed(wedge_name: String)

var _buttons: Array[CanvasItem] = []   # the wedge pictures (TextureButton or TouchScreenButton)
var _images: Array[Image] = []         # their pixels, for checking what's see-through
var _panels: Array[CanvasItem] = []    # each wedge's frosted-glass panel behind it (may be null)
var _hovered := -1
var _pressed := -1

func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	_collect(self)
	for button in _buttons:
		var texture: Texture2D = button.texture_normal
		var image := texture.get_image() if texture else null
		if image and image.is_compressed():
			image.decompress()
		_images.append(image)
		# The panels share materials: give each its own copy so only one lights up.
		var panel := button.get_parent() as CanvasItem
		if panel and panel.material is ShaderMaterial:
			panel.material = panel.material.duplicate()
			_panels.append(panel)
		else:
			_panels.append(null)

# Finds the wedge pictures, and stops every node here from taking mouse input itself
# (this script handles the mouse for all of them).
func _collect(node: Node) -> void:
	for child in node.get_children():
		if child is Control:
			child.mouse_filter = Control.MOUSE_FILTER_IGNORE
		if child.is_in_group("radial_button"):
			_buttons.append(child)
		_collect(child)

## Index of the wedge at this point on the screen, or -1.
func _wedge_at(screen_pos: Vector2) -> int:
	var best := -1
	var best_alpha := ALPHA_THRESHOLD
	for i in _buttons.size():
		var image := _images[i]
		if image == null:
			continue
		var p: Vector2 = _buttons[i].get_global_transform_with_canvas().affine_inverse() * screen_pos
		var x := int(p.x)
		var y := int(p.y)
		if x < 0 or y < 0 or x >= image.get_width() or y >= image.get_height():
			continue
		var alpha := image.get_pixel(x, y).a
		if alpha > best_alpha:   # soft edges can overlap: the most solid one wins
			best_alpha = alpha
			best = i
	return best

func _input(event: InputEvent) -> void:
	if not is_visible_in_tree():
		return
	if event is InputEventMouseMotion:
		_set_hovered(_wedge_at(event.position))
	elif event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		var inside := _inside_circle(event.position)
		if event.pressed:
			if not inside:
				return   # outside the menu: let Main_Control close it
			_pressed = _wedge_at(event.position)
			_refresh()
			get_viewport().set_input_as_handled()
		elif _pressed != -1 or inside:
			var released_on := _wedge_at(event.position)
			if _pressed != -1 and released_on == _pressed:
				wedge_pressed.emit(_buttons[_pressed].get_parent().name)
			_pressed = -1
			_refresh()
			get_viewport().set_input_as_handled()

func _inside_circle(screen_pos: Vector2) -> bool:
	var center: Vector2 = global_position + (size * 0.5)
	return center.distance_to(screen_pos) <= MENU_RADIUS

func _set_hovered(index: int) -> void:
	if index == _hovered:
		return
	_hovered = index
	_refresh()

func _refresh() -> void:
	for i in _buttons.size():
		var tint := 1.0
		if i == _pressed and i == _hovered:
			tint = PRESSED_TINT
		elif i == _hovered and _pressed == -1:
			tint = HOVER_TINT
		_buttons[i].modulate = Color(tint, tint, tint)
		if _panels[i]:
			(_panels[i].material as ShaderMaterial).set_shader_parameter("tint", tint)
