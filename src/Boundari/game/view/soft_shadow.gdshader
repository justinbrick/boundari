shader_type canvas_item;

// A soft drop shadow: the text's shape (drawn into a SubViewport by StartupSplash.cs),
// blurred with Gaussian weights and filled with one colour.

uniform vec4 shadow_color : source_color = vec4(0.0, 0.0, 0.0, 0.7);
uniform float blur_px = 12.0; // how far the shadow spreads, in pixels

const int TAPS = 7; // samples each side of the centre, per axis

varying float fade; // the node's modulate alpha (the splash fades in and out with it)

void vertex() {
	fade = COLOR.a;
}

void fragment() {
	float sigma = max(blur_px * 0.5, 0.001);
	float sum = 0.0;
	float total = 0.0;
	for (int j = -TAPS; j <= TAPS; j++) {
		for (int i = -TAPS; i <= TAPS; i++) {
			vec2 offset = vec2(float(i), float(j)) * (blur_px / float(TAPS));
			float w = exp(-dot(offset, offset) / (2.0 * sigma * sigma));
			sum += texture(TEXTURE, UV + offset * TEXTURE_PIXEL_SIZE).a * w;
			total += w;
		}
	}
	COLOR = vec4(shadow_color.rgb, shadow_color.a * fade * sum / total);
}
