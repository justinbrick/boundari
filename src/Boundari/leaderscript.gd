extends HBoxContainer

@export var first_color: Color = Color(1.0, 0.84, 0.0, 1.0)
@export var second_color: Color = Color(0.75, 0.75, 0.75, 1.0)
@export var third_color: Color = Color(0.8, 0.5, 0.2, 1.0)
@export var stroke_width: float = 3.0
@export var corner_radius: float = 24.0

func _ready() -> void:
	apply_outlines()

func apply_outlines() -> void:
	var icons = get_children()

	for i in icons.size():
		var icon = icons[i]
		if not icon is TextureRect:
			continue
		if not icon.material is ShaderMaterial:
			push_error("TextureRect at index %d is missing a ShaderMaterial" % i)
			continue

		icon.material.set_shader_parameter("stroke_width_px", stroke_width)
		icon.material.set_shader_parameter("corner_radius_px", corner_radius)

		match i:
			0:
				icon.material.set_shader_parameter("stroke_enabled", true)
				icon.material.set_shader_parameter("stroke_color", first_color)
			1:
				icon.material.set_shader_parameter("stroke_enabled", true)
				icon.material.set_shader_parameter("stroke_color", second_color)
			2:
				icon.material.set_shader_parameter("stroke_enabled", true)
				icon.material.set_shader_parameter("stroke_color", third_color)
			_:
				icon.material.set_shader_parameter("stroke_enabled", false)
