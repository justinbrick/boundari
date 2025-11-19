extends Button

@export var phase_icons: Array[Texture2D] = []   # your 3 icons
var current_phase: int = 0

func _ready() -> void:
    pressed.connect(_on_pressed)

    if phase_icons.is_empty():
        push_warning("phase_icons is empty — assign textures in the Inspector!")
        return

    _update_visual()


func _on_pressed() -> void:
    if phase_icons.is_empty():
        return

    # Cycle: 0 → 1 → 2 → 0 → ...
    current_phase = (current_phase + 1) % phase_icons.size()
    _update_visual()


func _update_visual() -> void:
    # Set the icon for the current phase
    icon = phase_icons[current_phase]