extends CanvasLayer

@onready var options_menu: Control = $OptionsMenu  # adjust path to your Control node

const SLIDE_DURATION := 0.18  # seconds — tweak for speed
var is_open := false
var menu_height: float

func _ready() -> void:
    menu_height = options_menu.size.y
    # Start hidden above the screen
    options_menu.position.y = -menu_height
    options_menu.visible = false

func _unhandled_input(event: InputEvent) -> void:
    if event.is_action_pressed("ui_cancel"):  # ui_cancel = ESC by default
        if is_open:
            _close_menu()
        else:
            _open_menu()

func _open_menu() -> void:
    is_open = true
    options_menu.position.y = -menu_height
    options_menu.visible = true

    var tween := create_tween()
    tween.set_ease(Tween.EASE_OUT)
    tween.set_trans(Tween.TRANS_QUINT)
    tween.tween_property(options_menu, "position:y", 0.0, SLIDE_DURATION)

func _close_menu() -> void:
    is_open = false

    var tween := create_tween()
    tween.set_ease(Tween.EASE_IN)
    tween.set_trans(Tween.TRANS_QUINT)
    tween.tween_property(options_menu, "position:y", -menu_height, SLIDE_DURATION)
    await tween.finished
    options_menu._reset_panels()
    options_menu.visible = false