shader_type canvas_item;

// The radial menu's alliance wedge when the player you right-clicked has asked you to
// ally: a thick green stroke just inside the wedge's outline, with two lighter green
// glows chasing each other clockwise around it. The texture is the wedge picture itself,
// used only for its shape (alpha).

uniform vec2 centre = vec2(0.75, 0.75);       // middle of the wedge, in UV
uniform float tex_size = 380.0;               // texture size in pixels
uniform float stroke_px = 16.0;               // stroke thickness, in texture pixels
uniform float turns_per_second = 0.32;
uniform vec4 stroke_color : source_color = vec4(0.16, 0.58, 0.2, 1.0);
uniform vec4 glow_color : source_color = vec4(0.72, 1.0, 0.62, 1.0);


float inside(sampler2D tex, vec2 uv) {
	return smoothstep(0.02, 0.08, texture(tex, uv).a);
}

// One glow: bright at its head, fading out along a tail behind it (behind = counter-clockwise).
float glow(float t, float head) {
	float behind = fract(head - t);   // 0 at the head, growing along the tail
	float ahead = fract(t - head);    // 0 at the head, growing in front of it
	float tail = exp(-behind * 9.0);
	float front = exp(-pow(ahead / 0.025, 2.0));
	return behind < 0.5 ? tail : front;
}

void fragment() {
	float here = inside(TEXTURE, UV);
	// How much of a ring of points stroke_px away lies outside the wedge: above 0 means
	// this point is within a stroke's width of the outline.
	float outside = 0.0;
	float r = stroke_px / tex_size;
	for (int i = 0; i < 24; i++) {
		float a = float(i) / 24.0 * TAU;
		outside += 1.0 - inside(TEXTURE, UV + vec2(cos(a), sin(a)) * r);
	}
	outside /= 24.0;
	// Half as far out too, so the stroke stays solid right up to the outline.
	float outside_half = 0.0;
	for (int i = 0; i < 12; i++) {
		float a = (float(i) + 0.5) / 12.0 * TAU;
		outside_half += 1.0 - inside(TEXTURE, UV + vec2(cos(a), sin(a)) * r * 0.5);
	}
	outside_half /= 12.0;
	float stroke = max(smoothstep(0.0, 0.06, outside), smoothstep(0.0, 0.1, outside_half)) * here;

	// Position around the wedge: screen y points down, so a growing angle is clockwise.
	vec2 d = UV - centre;
	float t = atan(d.y, d.x) / TAU + 0.5;
	float head = fract(TIME * turns_per_second);
	float g = max(glow(t, head), glow(t, fract(head + 0.5)));

	vec3 col = mix(stroke_color.rgb, glow_color.rgb, g);
	COLOR = vec4(col, stroke * mix(0.9, 1.0, g));
}
