extends Control

@export var popup_offset_y: float = 4.0   # how high above the button the panel sits

@onready var buttons_row: HBoxContainer = $ButtonsRow
@onready var info_panel: Panel = $InfoPanel
@onready var vbox: VBoxContainer = $InfoPanel/VBoxContainer
@onready var label1: Label = $InfoPanel/VBoxContainer/Label
@onready var label2: Label = $InfoPanel/VBoxContainer/Label2
@onready var hbox_cash: HBoxContainer = $InfoPanel/VBoxContainer/HBox_Cash
@onready var label3: Label = $InfoPanel/VBoxContainer/HBox_Cash/Label3


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


func _on_button_mouse_entered(button: Button) -> void:
	# Read the 3 exported lines from the button's script (hoveroverhaul.gd)
	label1.text = button.line1
	label2.text = button.line2
	label3.text = button.line3

	# No more resizing here — use whatever size you set in the editor
	var panel_size := info_panel.size
	if panel_size == Vector2.ZERO:
		# Safety: if you never sized it, at least avoid 0x0
		panel_size = info_panel.get_combined_minimum_size()

	# Get button's global rect
	var br: Rect2 = button.get_global_rect()

	# Position the panel centered above the button
	var pos: Vector2 = br.position
	pos.x += (br.size.x - panel_size.x) / 2.0
	pos.y -= panel_size.y + popup_offset_y  # use adjustable offset

	info_panel.global_position = pos
	info_panel.show()


func _on_button_mouse_exited(_button: Button) -> void:
	info_panel.hide()
