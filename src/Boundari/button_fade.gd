extends Button

@export var fade_duration: float = 0.15

var _hover_style: StyleBoxFlat
var _target_alpha: float
var _tween: Tween

func _ready() -> void:
	_hover_style = get_theme_stylebox("hover").duplicate()
	_target_alpha = _hover_style.bg_color.a
	_hover_style.bg_color.a = 0.0
	add_theme_stylebox_override("hover", _hover_style)
	add_theme_stylebox_override("normal", _hover_style)
	mouse_entered.connect(_fade.bind(true))
	mouse_exited.connect(_fade.bind(false))

func _fade(hovered: bool) -> void:
	if _tween:
		_tween.kill()
	_tween = create_tween().set_ease(Tween.EASE_OUT).set_trans(Tween.TRANS_SINE)
	_tween.tween_method(
		func(a: float) -> void: _hover_style.bg_color.a = a,
		_hover_style.bg_color.a,
		_target_alpha if hovered else 0.0,
		fade_duration
	)
