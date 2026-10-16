extends Control

# The radial menu's wedges. Every wedge picture is a full 380x380 image with the wedge
# drawn in its own part and the rest see-through, so all the buttons cover the same
# square and Godot can't tell which one the mouse is on. Instead this script looks at
# the pictures itself: the wedge under the mouse is the one whose picture isn't
# see-through there. That wedge lightens on hover and darkens while pressed.
#
# The alliance wedge (bottom right) can also show a moving green outline: the game
# turns it on (set_alliance_pending) when the player you right-clicked has asked you
# to ally. See alliance_pulse.gdshader.
#
# The game can also restyle a wedge (set_wedge_state): tint it, dim it so it can't be
# clicked, or lay a short label over it (e.g. "Break alliance" on the target wedge).

# 380x380 → radius 190
const MENU_RADIUS := 190.0
const ALPHA_THRESHOLD := 0.02         # less opaque than this counts as "not on the wedge" (the spokes are very faint)
# How much lighter/darker a wedge gets: its frosted-glass panel (blur.gdshader's tint) and its picture.
const HOVER_TINT := 1.45
const PRESSED_TINT := 0.6
const DISABLED_COLOUR := Color(0.5, 0.5, 0.5, 0.7)   # a wedge that can't be used right now
const DISABLED_PANEL_TINT := 0.75
const ALLIANCE_WEDGE := "BR_Rect"
const PULSE_SHADER := preload("res://alliance_pulse.gdshader")

# Wedge pictures' pixels, read once and shared by every menu opened afterwards.
static var _image_cache := {}

## Sent when a wedge is clicked (pressed and released on the same wedge). The name is the
## wedge's holder node, e.g. "T_Rect" or "Center_Rect".
signal wedge_pressed(wedge_name: String)

var _buttons: Array[CanvasItem] = []   # the wedge pictures (TextureButton or TouchScreenButton)
var _images: Array[Image] = []         # their pixels, for checking what's see-through
var _panels: Array[CanvasItem] = []    # each wedge's frosted-glass panel behind it (may be null)
var _hovered := -1
var _pressed := -1
var _enabled: Array[bool] = []         # per wedge: false = dimmed and not clickable
var _colours: Array[Color] = []        # per wedge: tint multiplied into its picture
var _labels: Array[Label] = []         # per wedge: optional text laid over it
var _pulse: TextureRect = null

func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	_collect(self)
	for button in _buttons:
		var texture: Texture2D = button.texture_normal
		var image: Image = _image_cache.get(texture)
		if image == null and texture:
			image = texture.get_image()
			if image and image.is_compressed():
				image.decompress()
			_image_cache[texture] = image
		_images.append(image)
		_enabled.append(true)
		_colours.append(Color.WHITE)
		_labels.append(null)
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
			if _pressed != -1 and not _enabled[_pressed]:
				_pressed = -1   # dimmed: the click is swallowed, nothing happens
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
	# Through the menu's transform, so it still works when the menu is scaled.
	var xf := get_global_transform_with_canvas()
	var center: Vector2 = xf * (size * 0.5)
	return center.distance_to(screen_pos) <= MENU_RADIUS * xf.get_scale().x

## Shows (or hides) the moving green outline on the alliance wedge.
func set_alliance_pending(on: bool) -> void:
	if not on:
		if _pulse:
			_pulse.queue_free()
			_pulse = null
		return
	if _pulse:
		return
	var i := -1
	for k in _buttons.size():
		if _buttons[k].get_parent().name == ALLIANCE_WEDGE:
			i = k
	if i < 0 or _images[i] == null:
		return
	var button := _buttons[i]
	var material := ShaderMaterial.new()
	material.shader = PULSE_SHADER
	material.set_shader_parameter("centre", _centroid(_images[i]) / Vector2(_images[i].get_size()))
	material.set_shader_parameter("tex_size", float(_images[i].get_width()))
	_pulse = TextureRect.new()
	_pulse.texture = button.texture_normal
	_pulse.material = material
	_pulse.mouse_filter = Control.MOUSE_FILTER_IGNORE
	button.get_parent().add_child(_pulse)   # after the wedge picture, so it's drawn on top
	_pulse.position = button.position

## Restyles a wedge: enabled = can be hovered and clicked (otherwise dimmed); colour =
## a tint multiplied into its picture; text = a label laid over the middle of the wedge
## ("" for none).
func set_wedge_state(wedge_name: String, enabled: bool, colour: Color = Color.WHITE, text: String = "") -> void:
	for i in _buttons.size():
		if _buttons[i].get_parent().name != wedge_name:
			continue
		_enabled[i] = enabled
		_colours[i] = colour
		if text == "":
			if _labels[i]:
				_labels[i].queue_free()
				_labels[i] = null
		else:
			if _labels[i] == null:
				var label := Label.new()
				label.mouse_filter = Control.MOUSE_FILTER_IGNORE
				label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
				label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
				label.add_theme_font_size_override("font_size", 22)
				label.add_theme_color_override("font_outline_color", Color(0, 0, 0, 0.9))
				label.add_theme_constant_override("outline_size", 8)
				var holder := _buttons[i].get_parent() as Control
				holder.add_child(label)
				var middle := _centroid(_images[i]) if _images[i] else Vector2(190, 190)
				label.size = Vector2(160, 70)
				label.position = middle - label.size * 0.5
				_labels[i] = label
			_labels[i].text = text
	if _hovered != -1 and not _enabled[_hovered]:
		_hovered = -1
	_refresh()

## Middle of a wedge picture's visible part, in pixels.
func _centroid(image: Image) -> Vector2:
	var sum := Vector2.ZERO
	var count := 0.0
	for y in range(0, image.get_height(), 3):
		for x in range(0, image.get_width(), 3):
			var a := image.get_pixel(x, y).a
			if a > ALPHA_THRESHOLD:
				sum += Vector2(x, y) * a
				count += a
	return sum / count if count > 0.0 else Vector2(image.get_size()) * 0.5

func _set_hovered(index: int) -> void:
	if index != -1 and not _enabled[index]:
		index = -1   # dimmed wedges don't light up
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
		var colour := _colours[i] * Color(tint, tint, tint)
		if not _enabled[i]:
			colour = _colours[i] * DISABLED_COLOUR
		_buttons[i].modulate = colour
		if _panels[i]:
			(_panels[i].material as ShaderMaterial).set_shader_parameter("tint", tint if _enabled[i] else DISABLED_PANEL_TINT)
