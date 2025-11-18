shader_type canvas_item;

uniform float shadow_size = 8.0;
uniform vec4 shadow_color @hint_color = vec4(0.0, 0.0, 0.0, 0.4);

void fragment() {
    float alpha = texture(TEXTURE, UV).a;
    float shadow = 0.0;
    
    for (int x = -4; x <= 4; x++) {
        for (int y = -4; y <= 4; y++) {
            vec2 offset = vec2(x, y) / (shadow_size * 100.0);
            shadow += texture(TEXTURE, UV + offset).a;
        }
    }
    
    shadow /= 81.0; // number of samples
    
    // Draw shadow
    COLOR = vec4(shadow_color.rgb, shadow_color.a * shadow);

    // Draw original rect on top
    COLOR = mix(COLOR, vec4(1.0), alpha);
}