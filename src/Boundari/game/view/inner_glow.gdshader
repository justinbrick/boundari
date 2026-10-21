shader_type canvas_item;

// A soft glow just inside a rounded rectangle, strongest at the edge and fading inwards
// (the notification bar's flash). The ColorRect sits inside the panel's border, so the
// glow never covers the border itself.

uniform vec4 glow_color : source_color = vec4(0.75, 0.83, 0.92, 0.35);
uniform float amount = 0.0;        // 0 = off, 1 = full flash
uniform float width_px = 14.0;     // how far in the glow reaches
uniform float radius_px = 4.0;     // corner rounding, matching the panel's inner edge
uniform vec2 rect_size = vec2(100.0, 40.0);

float rounded_box(vec2 p, vec2 half_size, float r) {
	vec2 q = abs(p) - half_size + r;
	return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void fragment() {
	vec2 p = UV * rect_size - rect_size * 0.5;
	float inside = -rounded_box(p, rect_size * 0.5, radius_px); // px in from the edge
	float edge = clamp(inside + 0.5, 0.0, 1.0);                 // smooth outer edge
	float fade = 1.0 - clamp(inside / width_px, 0.0, 1.0);
	COLOR = vec4(glow_color.rgb, glow_color.a * fade * fade * edge * amount);
}
