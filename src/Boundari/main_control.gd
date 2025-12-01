extends Control

@export var popup_offset_y: float = 4.0   # how high above the button the panel sits

@onready var buttons_row: HBoxContainer = $ButtonsRow
@onready var info_panel: Panel = $InfoPanel
@onready var vbox: VBoxContainer = $InfoPanel/VBoxContainer
@onready var label1: Label = $InfoPanel/VBoxContainer/Label
@onready var label2: Label = $InfoPanel/VBoxContainer/Label2
@onready var hbox_cash: HBoxContainer = $InfoPanel/VBoxContainer/HBox_Cash
@onready var label3: Label = $InfoPanel/VBoxContainer/HBox_Cash/Label3

# ============================================================
# RADIAL MENU SETUP
# ============================================================
var RadialMenuScene := preload("res://RadialMenu.tscn")   # <-- update if needed
var current_menu: Control = null



func _ready() -> void:
	# Start hidden and make sure the popup never interferes with the mouse
	info_panel.visible = false
	info_panel.mouse_filter = Control.MOUSE_FILTER_IGNORE
	vbox.mouse_filter = Control.MOUSE_FILTER_IGNORE
	hbox_cash.mouse_filter = Control.MOUSE_FILTER_IGNORE

	# All children inside vbox (including HBox_Cash children) ignore mouse
	for child in vbox.get_children():
		if child is Control:
			child.mouse_filter = Control.MOUSE_FILTER_IGNORE
			for grandchild in child.get_children():
				if grandchild is Control:
					grandchild.mouse_filter = Control.MOUSE_FILTER_IGNORE

	# Connect hover signals for each button under each wrapper
	for wrapper in buttons_row.get_children():
		if wrapper is Control:
			var button: Button = null
			for c in wrapper.get_children():
				if c is Button:
					button = c
					break

			if button:
				button.mouse_entered.connect(_on_button_mouse_entered.bind(button))
				button.mouse_exited.connect(_on_button_mouse_exited.bind(button))



# ============================================================
# INFO PANEL HOVER LOGIC
# ============================================================
func _on_button_mouse_entered(button: Button) -> void:
	# Read the 3 exported lines from the button's script (hoveroverhaul.gd)
	label1.text = button.line1
	label2.text = button.line2
	label3.text = button.line3

	var panel_size := info_panel.size
	if panel_size == Vector2.ZERO:
		panel_size = info_panel.get_combined_minimum_size()

	# Get button's global rect
	var br: Rect2 = button.get_global_rect()

	# Position the panel centered above the button
	var pos: Vector2 = br.position
	pos.x += (br.size.x - panel_size.x) / 2.0
	pos.y -= panel_size.y + popup_offset_y

	info_panel.global_position = pos
	info_panel.show()


func _on_button_mouse_exited(_button: Button) -> void:
	info_panel.hide()



# ============================================================
# RADIAL MENU INPUT LOGIC
# ============================================================
func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseButton:

		# Right-click → open radial menu
		if event.button_index == MOUSE_BUTTON_RIGHT and event.pressed:
			show_radial_menu(event.position)
			return

		# Left-click → close radial menu
		if event.button_index == MOUSE_BUTTON_LEFT and event.pressed:
			hide_radial_menu()



# ============================================================
# RADIAL MENU SPAWN / CLOSE (UPDATED POSITIONING)
# ============================================================
func show_radial_menu(mouse_pos: Vector2) -> void:
	# Remove old one if still open
	if current_menu:
		current_menu.queue_free()

	# Instance new menu
	current_menu = RadialMenuScene.instantiate()
	add_child(current_menu)

	# 🔥 IMPORTANT: wait until layout is finished so size is correct
	await get_tree().process_frame

	# Safety fallback (only used if your radial menu root somehow has no size)
	if current_menu.size == Vector2.ZERO:
		current_menu.size = Vector2(380, 380)

	# ⭐ FORCE global alignment AFTER layout is done ⭐
	current_menu.set_global_position(mouse_pos - (current_menu.size * 0.5))


func hide_radial_menu() -> void:
	if current_menu:
		current_menu.queue_free()
		current_menu = null