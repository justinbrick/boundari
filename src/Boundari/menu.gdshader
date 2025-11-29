shader_type canvas_item;

// Scroll speed for infinite left movement
uniform float scroll_speed : hint_range(0.0, 1.0) = 0.05;

// Strength of distortion (positive OR negative)
//  0.0  = no distortion
//  0.3  = barrel (outward bulge)
// -0.3  = pincushion (inward bulge)
uniform float distortion_strength : hint_range(-1.0, 1.0) = 0.35;

void fragment() {
    // Base UV stays locked to the frame (lens space)
    vec2 base_uv = UV;

    // ----- LENS DISTORTION (both directions supported) -----
    vec2 center = vec2(0.5, 0.5);
    vec2 offset = base_uv - center;
    float r = length(offset);

    // k > 0  = fisheye / barrel (bulges outward)
    // k < 0  = pincushion (bulges inward)
    float k = distortion_strength;

    // Same formula works for positive or negative k
    float nr = r * (1.0 + k * r * r);

    vec2 lens_uv;
    if (r > 0.0001) {
        lens_uv = center + offset * (nr / r);
    } else {
        lens_uv = base_uv;
    }

    // ----- SCROLL THE MAP UNDER THE LENS -----
    lens_uv.x += TIME * scroll_speed;

    // Tile horizontally
    vec2 sample_uv = vec2(fract(lens_uv.x), lens_uv.y);

    // Sample the texture
    vec4 tex = texture(TEXTURE, sample_uv);

    COLOR = tex;
}