extends Control

@onready var buttons_row: HBoxContainer = $ButtonsRow
@onready var info_panel: Panel = $InfoPanel
@onready var label1: Label = $InfoPanel/Label1
@onready var label2: Label = $InfoPanel/Label2
@onready var label3: Label = $InfoPanel/Label3

func _ready() -> void:
    for child in buttons_row.get_children():
        if child is Button:
            child.mouse_entered.connect(_on_button_mouse_entered.bind(child))
            child.mouse_exited.connect(_on_button_mouse_exited.bind(child))


func _on_button_mouse_entered(button: Button) -> void:
    # If you used the InfoButton script, cast the button:
    var info_button := button as InfoButton
    if info_button:
        label1.text = info_button.label1_text
        label2.text = info_button.label2_text
        label3.text = info_button.label3_text
    else:
        # fallback if no custom script
        label1.text = button.text
        label2.text = ""
        label3.text = ""

    # Let the panel size itself to the labels
    info_panel.reset_size()

    # Position panel centered above the hovered button
    var br: Rect2 = button.get_global_rect()
    var panel_size: Vector2 = info_panel.size

    var pos := br.position
    pos.x += (br.size.x - panel_size.x) / 2.0
    pos.y -= panel_size.y + 4.0  # 4px gap above button

    info_panel.global_position = pos
    info_panel.show()


func _on_button_mouse_exited(button: Button) -> void:
    info_panel.hide()