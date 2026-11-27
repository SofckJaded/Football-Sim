shader_type spatial;
// Outer apron skirt ring around the inner turf plane (docs/football-grass.md Â§5.3). Deliberately
// fetch-free and flat: this ring only exists to guarantee the ground reads as ground out to the
// 65 m / 21:9 corner envelope (~200 m). It receives shadows and never casts them.
render_mode cull_disabled;

// Must match PitchSurfaceBuilder's inner-mesh fade colour exactly, or the flush y = 0.0 seam shows.
uniform vec3 apron_color : source_color = vec3(0.158, 0.302, 0.161);
uniform vec3 apron_far_color : source_color = vec3(0.128, 0.224, 0.137);
uniform float roughness = 0.95;
// Distance over which the far colour takes over so the limit of the plane is not a flat wash.
uniform float far_near = 170.0;
uniform float far_far = 450.0;

void fragment() {
    // VERTEX is the fragment position in view space; the camera looks down -Z, so -VERTEX.z is depth.
    float dist = -VERTEX.z;
    ALBEDO = mix(apron_color, apron_far_color, smoothstep(far_near, far_far, dist));
    ROUGHNESS = roughness;
}

