extends Control

# Panels
@onready var profile_main: Control = $Profile_Main
@onready var video_main: Control = $Video_Main
@onready var audio_main: Control = $Audio_Main
@onready var gameplay_main: Control = $Gameplay_Main
@onready var keybinds_main: Control = $Keybinds_Main

# Buttons
@onready var profile_btn: Button = $Options_Panel/HBoxContainer/Profile
@onready var video_btn: Button = $Options_Panel/HBoxContainer/Video
@onready var audio_btn: Button = $Options_Panel/HBoxContainer/Audio
@onready var gameplay_btn: Button = $Options_Panel/HBoxContainer/Gameplay
@onready var keybinds_btn: Button = $Options_Panel/HBoxContainer/Keybinds

var all_panels: Array[Control]
var all_buttons: Array[Button]

func _ready() -> void:
    all_panels = [profile_main, video_main, audio_main, gameplay_main, keybinds_main]
    all_buttons = [profile_btn, video_btn, audio_btn, gameplay_btn, keybinds_btn]

    profile_btn.pressed.connect(_on_tab_pressed.bind(0))
    video_btn.pressed.connect(_on_tab_pressed.bind(1))
    audio_btn.pressed.connect(_on_tab_pressed.bind(2))
    gameplay_btn.pressed.connect(_on_tab_pressed.bind(3))
    keybinds_btn.pressed.connect(_on_tab_pressed.bind(4))

    _reset_panels()

func _reset_panels() -> void:
    active_index = -1
    for panel in all_panels:
        panel.visible = false

var active_index: int = -1

func _on_tab_pressed(index: int) -> void:
    if active_index == index:
        active_index = -1
        _reset_panels()
        return

    active_index = index
    for i in all_panels.size():
        all_panels[i].visible = (i == index)