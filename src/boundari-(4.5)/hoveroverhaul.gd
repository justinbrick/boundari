extends Button

# === Popup text lines for the info panel ===
@export var line1: String = ""
@export var line2: String = ""
@export var line3: String = ""

# How far the button jumps on hover (negative = up)
@export var hover_offset_y: float = -16.0
@export var hover_duration: float = 0.12

@onready var shader_mat: ShaderMaterial = (
    $TextureRect.material as ShaderMaterial
)

var base_position: Vector2
var move_tween: Tween


func _ready() -> void:
    # Save starting position (inside its wrapper Control)
    base_position = position

    # Connect signals in code so you don't have to use the editor
    mouse_entered.connect(_on_mouse_entered)
    mouse_exited.connect(_on_mouse_exited)
    button_down.connect(_on_button_down)
    button_up.connect(_on_button_up)

    # Initialize shader parameters
    if shader_mat:
        shader_mat.set_shader_parameter("hover_amount", 0.0)
        shader_mat.set_shader_parameter("pressed_amount", 0.0)


func _on_mouse_entered() -> void:
    _kill_tween()
    move_tween = create_tween()

    # 1 Move the button up by hover_offset_y
    move_tween.tween_property(
        self,
        "position",
        base_position + Vector2(0.0, hover_offset_y),
        hover_duration
    ).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)

    # 2 Animate hover_amount -> 1.0 on the shader
    if shader_mat:
        move_tween.parallel().tween_method(
            func(v): shader_mat.set_shader_parameter("hover_amount", v),
            shader_mat.get_shader_parameter("hover_amount"),
            1.0,
            hover_duration
        )


func _on_mouse_exited() -> void:
    _kill_tween()
    move_tween = create_tween()

    # 1 Move the button back to its base position
    move_tween.tween_property(
        self,
        "position",
        base_position,
        hover_duration
    ).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)

    # 2 Animate hover_amount -> 0.0 on the shader
    if shader_mat:
        move_tween.parallel().tween_method(
            func(v): shader_mat.set_shader_parameter("hover_amount", v),
            shader_mat.get_shader_parameter("hover_amount"),
            0.0,
            hover_duration
        )


func _on_button_down() -> void:
    if shader_mat:
        shader_mat.set_shader_parameter("pressed_amount", 1.0)


func _on_button_up() -> void:
    if shader_mat:
        shader_mat.set_shader_parameter("pressed_amount", 0.0)


func _kill_tween() -> void:
    if move_tween and move_tween.is_valid() and move_tween.is_running():
        move_tween.kill()