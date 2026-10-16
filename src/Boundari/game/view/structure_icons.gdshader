shader_type canvas_item;
render_mode unshaded;

// Building icons, one quad each (see StructureIcons.cs). The shape is worked out exactly
// from its distance field, so edges are crisp with one pixel of smoothing at any size;
// the glyph comes from an atlas of the white icon pictures.
//   UV.x: which building (atlas column) and where across it.
//   UV.y: the mode (whole part) and where down it: 0 shape + glyph, 1 shape only,
//         2 / 3 placement preview with a green / red outline.
//   COLOR: the owner's colour, and the icon's opacity.

uniform sampler2D glyphs : filter_linear_mipmap, repeat_disable;
uniform int columns = 6;
// Per column: sides (below 3 = circle), rotation (radians), circumradius (in icon sizes).
uniform vec3 shapes[6];
uniform float quad = 1.5;          // the quad is this many icon sizes across
uniform float outline = 0.09;      // outline thickness, in icon sizes
uniform float glyph_size = 0.55;   // the glyph's box, in icon sizes
uniform vec4 valid_outline : source_color = vec4(0.45, 0.95, 0.5, 1.0);
uniform vec4 invalid_outline : source_color = vec4(1.0, 0.35, 0.3, 1.0);

// Signed distance to the shape's edge (negative inside), in icon sizes.
float shape_distance(vec2 p, vec3 s) {
	if (s.x < 2.5) {
		return length(p) - s.z;
	}
	float slice = TAU / s.x;
	// The nearest edge's outward normal: halfway between two corners.
	float a = atan(p.y, p.x) - s.y - slice * 0.5;
	float normal = s.y + slice * 0.5 + round(a / slice) * slice;
	return dot(p, vec2(cos(normal), sin(normal))) - s.z * cos(PI / s.x);
}

// The owner's colour and opacity, as given to the draw call (in fragment(), COLOR already
// has the atlas texture multiplied in).
varying vec4 tint;

void vertex() {
	tint = COLOR;
}

void fragment() {
	float across = UV.x * float(columns);
	int column = clamp(int(floor(across)), 0, columns - 1);
	float mode = floor(UV.y);
	vec2 local = vec2(across - float(column), UV.y - mode);
	vec2 p = (local - 0.5) * quad;

	float d = shape_distance(p, shapes[column]);
	float aa = max(fwidth(d), 1e-5) * 0.5; // half a pixel each side: one crisp pixel of smoothing
	float half_line = outline * 0.5;
	float coverage = 1.0 - smoothstep(half_line - aa, half_line + aa, d);
	float inside = 1.0 - smoothstep(-half_line - aa, -half_line + aa, d);

	vec3 owner = tint.rgb;
	vec3 fill = mix(owner, vec3(1.0), 0.35);
	vec3 line = owner * 0.55;
	if (mode > 1.5) {
		line = mode < 2.5 ? valid_outline.rgb : invalid_outline.rgb;
	}
	vec3 colour = mix(line, fill, inside);

	if (mode < 0.5 || mode > 1.5) {
		vec2 g = p / glyph_size + 0.5;
		if (g.x >= 0.0 && g.y >= 0.0 && g.x <= 1.0 && g.y <= 1.0) {
			// A slight negative bias keeps the glyph sharp when it's drawn small.
			float glyph = texture(glyphs, vec2((float(column) + g.x) / float(columns), g.y), -0.6).a;
			colour = mix(colour, owner * 0.4, glyph * inside);
		}
	}
	COLOR = vec4(colour, coverage * tint.a);
}
