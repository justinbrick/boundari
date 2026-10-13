extends Panel

var button_map = {}
var label_map = {}

func _ready():
	button_map = {
		$VBoxContainer2/Skins_Button: $VBoxContainer2/Skins_Button/HBoxContainer5/Button_Circle,
		$VBoxContainer2/Names_Button: $VBoxContainer2/Names_Button/HBoxContainer5/Button_Circle,
		$VBoxContainer2/Lobby_Button: $VBoxContainer2/Lobby_Button/HBoxContainer5/Button_Circle,
		$VBoxContainer2/Pro_Button: $VBoxContainer2/Pro_Button/HBoxContainer5/Button_Circle,
		$VBoxContainer2/Hints_Button: $VBoxContainer2/Hints_Button/HBoxContainer5/Button_Circle,
		$VBoxContainer2/Perform_Button: $VBoxContainer2/Perform_Button/HBoxContainer5/Button_Circle,
	}

	label_map = {
		$VBoxContainer2/Skins_Button: [$VBoxContainer2/Skins_Button/HBoxContainer5/On_Label, $VBoxContainer2/Skins_Button/HBoxContainer5/Off_Label],
		$VBoxContainer2/Names_Button: [$VBoxContainer2/Names_Button/HBoxContainer5/On_Label, $VBoxContainer2/Names_Button/HBoxContainer5/Off_Label],
		$VBoxContainer2/Lobby_Button: [$VBoxContainer2/Lobby_Button/HBoxContainer5/On_Label, $VBoxContainer2/Lobby_Button/HBoxContainer5/Off_Label],
		$VBoxContainer2/Pro_Button: [$VBoxContainer2/Pro_Button/HBoxContainer5/On_Label, $VBoxContainer2/Pro_Button/HBoxContainer5/Off_Label],
		$VBoxContainer2/Hints_Button: [$VBoxContainer2/Hints_Button/HBoxContainer5/On_Label, $VBoxContainer2/Hints_Button/HBoxContainer5/Off_Label],
		$VBoxContainer2/Perform_Button: [$VBoxContainer2/Perform_Button/HBoxContainer5/On_Label, $VBoxContainer2/Perform_Button/HBoxContainer5/Off_Label],
	}

	for button in button_map:
		button.pressed.connect(_on_row_pressed.bind(button))

func _on_row_pressed(button):
	var toggle = button_map[button]
	toggle.button_pressed = !toggle.button_pressed

	var on_label = label_map[button][0]
	var off_label = label_map[button][1]
	on_label.visible = toggle.button_pressed
	off_label.visible = !toggle.button_pressed
