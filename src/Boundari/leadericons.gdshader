shader_type canvas_item;

// Corner radius in *pixels*
uniform float corner_radius_px : hint_range(0.0, 256.0) = 24.0;
// Edge feathering (anti-alias) in *pixels*
uniform float edge_softness_px : hint_range(0.0, 32.0) = 2.0;

// Overall opacity of the TextureRect
uniform float panel_alpha : hint_range(0.0, 1.0) = 1.0;


void fragment() {
    vec4 tex = texture(TEXTURE, UV);

    // Texture size in pixels
    vec2 tex_size = 1.0 / TEXTURE_PIXEL_SIZE;

    // Convert pixel radius / softness to UV units
    float min_dim = min(tex_size.x, tex_size.y);
    float r = corner_radius_px / min_dim;
    float soft = edge_softness_px / min_dim;

    // Clamp radius so it cannot exceed half the dimension
    float max_r = 0.5 - soft;
    r = clamp(r, 0.0, max_r);

    // Signed distance to a rounded rectangle in UV space
    vec2 uv = UV;
    vec2 half = vec2(0.5);
    vec2 q = abs(uv - half) - (half - vec2(r));
    vec2 q_clamped = max(q, vec2(0.0));
    float dist = length(q_clamped) - r;

    // Rounded mask with soft edges
    float mask = 1.0 - smoothstep(-soft, soft, dist);

    // Apply mask and opacity
    COLOR = tex * mask;
    COLOR.a *= panel_alpha;
}