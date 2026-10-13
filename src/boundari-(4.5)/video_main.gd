extends Control

@onready var display_menu: OptionButton = $Video_Panel/VBoxContainer/Display_HBox/Display_Menu
@onready var mode_menu: OptionButton = $Video_Panel/VBoxContainer/Mode_HBox/Mode_Menu
@onready var ratio_menu: OptionButton = $Video_Panel/VBoxContainer/Ratio_HBox/Ratio_Menu
@onready var res_menu: OptionButton = $Video_Panel/VBoxContainer/Res_HBox/Res_Menu
@onready var refresh_menu: OptionButton = $Video_Panel/VBoxContainer/Refresh_HBox/Refresh_Menu
@onready var apply_button: Button = $Video_Panel/VBoxContainer/HBoxContainer/Apply_Button

const RESOLUTIONS: Dictionary = {
	"4:3": [
		Vector2i(640, 480), Vector2i(800, 600), Vector2i(1024, 768),
		Vector2i(1280, 960), Vector2i(1400, 1050), Vector2i(1600, 1200)
	],
	"16:9": [
		Vector2i(1280, 720), Vector2i(1366, 768), Vector2i(1600, 900),
		Vector2i(1920, 1080), Vector2i(2560, 1440), Vector2i(3840, 2160)
	],
	"16:10": [
		Vector2i(1280, 800), Vector2i(1440, 900), Vector2i(1680, 1050),
		Vector2i(1920, 1200), Vector2i(2560, 1600)
	],
	"21:9": [
		Vector2i(2560, 1080), Vector2i(3440, 1440), Vector2i(3840, 1600)
	],
	"32:9": [
		Vector2i(3840, 1080), Vector2i(5120, 1440)
	]
}

const DISPLAY_MODES: Array[String] = ["Fullscreen", "Windowed", "Borderless"]
const ASPECT_RATIOS: Array[String] = ["4:3", "16:9", "16:10", "21:9", "32:9"]

var current_ratio: String = "16:9"
var current_mode: String = "Fullscreen"
var config := ConfigFile.new()
const SAVE_PATH := "user://settings.cfg"

var pending_monitor: int = -1
var pending_mode: int = -1
var pending_ratio: String = ""
var pending_res: int = -1
var pending_refresh: int = -1

func _ready() -> void:
	_load_settings()
	_populate_display()
	_populate_mode()
	_populate_ratio()
	_populate_resolutions(current_ratio)
	_populate_refresh()
	_update_availability()

	display_menu.item_selected.connect(_on_display_selected)
	mode_menu.item_selected.connect(_on_mode_selected)
	ratio_menu.item_selected.connect(_on_ratio_selected)
	res_menu.item_selected.connect(_on_res_selected)
	refresh_menu.item_selected.connect(_on_refresh_selected)
	apply_button.pressed.connect(_on_apply_pressed)

# --- Populate ---

func _populate_display() -> void:
	display_menu.clear()
	var screen_count: int = DisplayServer.get_screen_count()
	for i in screen_count:
		display_menu.add_item("Monitor %d" % (i + 1), i)
	var current_screen: int = DisplayServer.window_get_current_screen()
	display_menu.select(current_screen)

func _populate_mode() -> void:
	mode_menu.clear()
	for mode: String in DISPLAY_MODES:
		mode_menu.add_item(mode)
	var idx: int = DISPLAY_MODES.find(current_mode)
	mode_menu.select(idx if idx >= 0 else 0)

func _populate_ratio() -> void:
	ratio_menu.clear()
	for ratio: String in ASPECT_RATIOS:
		ratio_menu.add_item(ratio)
	var idx: int = ASPECT_RATIOS.find(current_ratio)
	ratio_menu.select(idx if idx >= 0 else 0)

func _populate_resolutions(ratio: String) -> void:
	res_menu.clear()
	var res_list: Array[Vector2i] = []
	res_list.assign(RESOLUTIONS[ratio])
	for res: Vector2i in res_list:
		res_menu.add_item("%dX%d" % [res.x, res.y])
	res_menu.select(0)

func _populate_refresh() -> void:
	refresh_menu.clear()
	var screen: int = DisplayServer.window_get_current_screen()
	var native_refresh: int = int(DisplayServer.screen_get_refresh_rate(screen))
	var common: Array[int] = [30, 60, 75, 120, 144, 165, 240]
	var refresh_rates: Array[int] = []
	for rate: int in common:
		if rate <= native_refresh:
			refresh_rates.append(rate)
	if not native_refresh in refresh_rates:
		refresh_rates.append(native_refresh)
	refresh_rates.sort()
	for rate: int in refresh_rates:
		refresh_menu.add_item("%d Hz" % rate)
	var saved_refresh: int = config.get_value("video", "refresh_rate", native_refresh)
	var saved_label: String = "%d Hz" % saved_refresh
	for i in refresh_menu.item_count:
		if refresh_menu.get_item_text(i) == saved_label:
			refresh_menu.select(i)
			break

func _update_availability() -> void:
	var is_windowed: bool = (current_mode == "Windowed")
	var is_fullscreen: bool = (current_mode == "Fullscreen" or current_mode == "Borderless")
	refresh_menu.disabled = is_windowed
	ratio_menu.disabled = is_fullscreen
	res_menu.disabled = is_fullscreen

# --- Asterisk marker ---

func _mark_selected(button: OptionButton, index: int) -> void:
	for i in button.item_count:
		var text: String = button.get_item_text(i)
		if text.begins_with("* "):
			button.set_item_text(i, text.substr(2))
	var current_text: String = button.get_item_text(index)
	button.set_item_text(index, "* " + current_text)

func _clear_all_markers() -> void:
	for button: OptionButton in [display_menu, mode_menu, ratio_menu, res_menu, refresh_menu]:
		for i in button.item_count:
			var text: String = button.get_item_text(i)
			if text.begins_with("* "):
				button.set_item_text(i, text.substr(2))

# --- Callbacks ---

func _on_display_selected(id: int) -> void:
	pending_monitor = id
	_mark_selected(display_menu, id)

func _on_mode_selected(id: int) -> void:
	pending_mode = id
	_mark_selected(mode_menu, id)
	var preview_mode: String = DISPLAY_MODES[id]
	refresh_menu.disabled = (preview_mode == "Windowed")
	ratio_menu.disabled = (preview_mode == "Fullscreen" or preview_mode == "Borderless")
	res_menu.disabled = (preview_mode == "Fullscreen" or preview_mode == "Borderless")

func _on_ratio_selected(id: int) -> void:
	pending_ratio = ASPECT_RATIOS[id]
	_populate_resolutions(pending_ratio)
	_mark_selected(ratio_menu, id)

func _on_res_selected(id: int) -> void:
	pending_res = id
	_mark_selected(res_menu, id)

func _on_refresh_selected(id: int) -> void:
	pending_refresh = id
	_mark_selected(refresh_menu, id)

# --- Apply ---

func _on_apply_pressed() -> void:
	if pending_monitor >= 0:
		DisplayServer.window_set_current_screen(pending_monitor)
		_populate_refresh()
		config.set_value("video", "monitor", pending_monitor)
		pending_monitor = -1

	if pending_mode >= 0:
		var mode: String = DISPLAY_MODES[pending_mode]
		current_mode = mode
		match mode:
			"Fullscreen":
				DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_FULLSCREEN)
			"Windowed":
				DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
			"Borderless":
				DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_FULLSCREEN)
				DisplayServer.window_set_flag(DisplayServer.WINDOW_FLAG_BORDERLESS, true)
		config.set_value("video", "display_mode", mode)
		pending_mode = -1

	if not pending_ratio.is_empty():
		current_ratio = pending_ratio
		config.set_value("video", "aspect_ratio", current_ratio)
		pending_ratio = ""

	if pending_res >= 0:
		var res: Vector2i = RESOLUTIONS[current_ratio][pending_res]
		DisplayServer.window_set_size(res)
		config.set_value("video", "resolution", "%dX%d" % [res.x, res.y])
		pending_res = -1

	if pending_refresh >= 0:
		var label: String = refresh_menu.get_item_text(pending_refresh)
		var rate: int = int(label.replace("* ", "").replace(" Hz", ""))
		Engine.max_fps = rate
		config.set_value("video", "refresh_rate", rate)
		pending_refresh = -1

	_update_availability()
	_clear_all_markers()
	_save_settings()

# --- Save / Load ---

func _save_settings() -> void:
	config.save(SAVE_PATH)

func _load_settings() -> void:
	if config.load(SAVE_PATH) == OK:
		current_ratio = config.get_value("video", "aspect_ratio", "16:9")
		current_mode = config.get_value("video", "display_mode", "Fullscreen")
