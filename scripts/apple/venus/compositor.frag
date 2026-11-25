#version 450
#extension GL_GOOGLE_include_directive : require
#include "compositor-uniforms.glsl"

layout(location = 0) in vec2 uv;
layout(location = 1) in vec2 localPosition;
layout(location = 2) flat in vec2 size;
layout(location = 0) out vec4 pixel;

#ifdef SHARED_BUFFER
layout(std430, set = 0, binding = 1) readonly buffer GuestPixels
{
    uint words[];
};

vec4 readGuestPixel(ivec2 xy)
{
    xy = clamp(xy, ivec2(0), ivec2(source.xy) - ivec2(1));
    uint index = source.w + uint(xy.y) * source.z + uint(xy.x);
    vec4 rgba = unpackUnorm4x8(words[index]);
    return drawOptions.y == 2.0 ? rgba : rgba.bgra;
}

vec4 sampleImage(vec2 sampleUv)
{
    vec2 xy = clamp(sampleUv, vec2(0), vec2(1)) * vec2(source.xy) - 0.5;
    ivec2 base = ivec2(floor(xy));
    vec2 f = fract(xy);
    return mix(mix(readGuestPixel(base), readGuestPixel(base + ivec2(1, 0)), f.x),
               mix(readGuestPixel(base + ivec2(0, 1)), readGuestPixel(base + ivec2(1, 1)), f.x),
               f.y);
}
#else
layout(set = 0, binding = 1) uniform texture2D SourceTexture;
layout(set = 0, binding = 2) uniform sampler SourceSampler;

vec4 sampleImage(vec2 sampleUv)
{
    return texture(sampler2D(SourceTexture, SourceSampler), sampleUv);
}
#endif

vec3 wallpaper()
{
    // Plasma Mobile ("Kay" / "Next" inspired) layered luminous geometric aurora wallpaper
    vec2 p = uv - vec2(0.5);
    float aspect = size.x / max(size.y, 1.0);
    vec2 q = vec2(p.x * aspect, p.y);

    // Deep twilight sky gradient (top deep navy -> mid teal-indigo -> bottom royal violet)
    vec3 topCol = vec3(0.055, 0.095, 0.215);
    vec3 midCol = vec3(0.065, 0.175, 0.295);
    vec3 botCol = vec3(0.115, 0.085, 0.245);
    vec3 col = mix(topCol, midCol, smoothstep(0.0, 0.55, uv.y));
    col = mix(col, botCol, smoothstep(0.45, 1.0, uv.y));

    // Soft Plasma cyan & magenta aurora glows
    float cyanGlow = exp(-2.4 * dot(q - vec2(-0.22 * aspect, -0.18), q - vec2(-0.22 * aspect, -0.18)));
    float tealGlow = exp(-2.8 * dot(q - vec2(0.28 * aspect, 0.05), q - vec2(0.28 * aspect, 0.05)));
    float magentaGlow = exp(-2.6 * dot(q - vec2(0.18 * aspect, 0.36), q - vec2(0.18 * aspect, 0.36)));
    col += cyanGlow * vec3(0.06, 0.24, 0.34);
    col += tealGlow * vec3(0.04, 0.22, 0.28);
    col += magentaGlow * vec3(0.24, 0.08, 0.26);

    // Layered Plasma geometric wave ridges
    float wave1 = uv.y - (0.58 + 0.075 * sin(uv.x * 3.1 + 0.4) - 0.06 * uv.x);
    float layer1 = smoothstep(-0.004, 0.004, wave1);
    vec3 ridge1 = mix(vec3(0.08, 0.26, 0.38), vec3(0.07, 0.14, 0.26), smoothstep(0.0, 0.35, wave1));
    col = mix(col, ridge1, layer1 * 0.72);
    col += exp(-55.0 * abs(wave1)) * vec3(0.18, 0.52, 0.68) * 0.55;

    float wave2 = uv.y - (0.72 + 0.065 * sin(uv.x * 2.6 + 2.3) + 0.05 * uv.x);
    float layer2 = smoothstep(-0.004, 0.004, wave2);
    vec3 ridge2 = mix(vec3(0.14, 0.13, 0.32), vec3(0.06, 0.08, 0.18), smoothstep(0.0, 0.30, wave2));
    col = mix(col, ridge2, layer2 * 0.85);
    col += exp(-50.0 * abs(wave2)) * vec3(0.45, 0.28, 0.62) * 0.50;

    // Diagonal Plasma light ribbon
    float ribbon = (uv.y - 0.32) + 0.22 * (uv.x - 0.5) + 0.05 * sin(uv.x * 4.2);
    col += exp(-12.0 * abs(ribbon)) * vec3(0.09, 0.28, 0.40) * 0.50;
    col += exp(-75.0 * abs(ribbon)) * vec3(0.22, 0.58, 0.76) * 0.38;

    // Subtle vignette + dither grain to eliminate banding
    float grain = fract(52.9829189 * fract(dot(floor(gl_FragCoord.xy), vec2(0.06711056, 0.00583715))));
    return col * (1.0 - 0.18 * smoothstep(0.35, 1.05, length(p))) + (grain - 0.5) / 255.0;
}

float sdRoundedBox(vec2 p, vec2 b, float r)
{
    vec2 q = abs(p) - b + vec2(r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float sdSegment(vec2 p, vec2 a, vec2 b)
{
    vec2 pa = p - a, ba = b - a;
    float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-5), 0.0, 1.0);
    return length(pa - ba * h);
}

vec4 windowChrome()
{
    float radius = chrome.x;
    float left = chrome.y;
    float titleHeight = chrome.z;
    float right = chrome.w;
    vec2 p = localPosition;
    vec2 corner = vec2(radius - min(p.x, size.x - p.x), radius - p.y);
    float distanceToCorner = length(max(corner, 0.0)) - radius;
    float aa = max(0.5 * fwidth(distanceToCorner), 0.01);
    float coverage = radius > 0.0 ? 1.0 - smoothstep(-aa, aa, distanceToCorner) : 1.0;

    bool title = p.y < titleHeight && p.x >= left && p.x < right;
    return (title ? titleColor : color) * coverage;
}

vec4 roundedSolid()
{
    float r = min(chrome.x, min(size.x, size.y) * 0.5);
    float d = sdRoundedBox(localPosition - size * 0.5, size * 0.5, r);
    float aa = max(0.75 * fwidth(d), 0.5);
    float cov = 1.0 - smoothstep(-aa, aa, d);
    return color * cov;
}

vec4 roundedTexture(vec2 sampleUv)
{
    float r = min(chrome.x, min(size.x, size.y) * 0.5);
    float d = sdRoundedBox(localPosition - size * 0.5, size * 0.5, r);
    float aa = max(0.75 * fwidth(d), 0.5);
    float cov = (1.0 - smoothstep(-aa, aa, d)) * chrome.z;
    vec4 tex = sampleImage(sampleUv);
    if (drawOptions.y == 0.0)
        tex.a = 1.0;
    return tex * cov;
}

vec4 vectorIcon()
{
    int kind = int(chrome.x + 0.5);
    vec2 p = (localPosition / max(min(size.x, size.y), 1.0)) * 2.0 - vec2(size.x / max(min(size.x, size.y), 1.0), size.y / max(min(size.x, size.y), 1.0));
    float stroke = 0.12;
    float d = 10.0;

    if (kind == 0)
    {
        // Android Back (classic left-pointing triangle outline)
        vec2 v0 = vec2(-0.55, 0.0);
        vec2 v1 = vec2(0.45, -0.56);
        vec2 v2 = vec2(0.45, 0.56);
        d = min(sdSegment(p, v0, v1), min(sdSegment(p, v1, v2), sdSegment(p, v2, v0))) - stroke * 0.65;
    }
    else if (kind == 1)
    {
        // Android Home (classic circle ring)
        d = abs(length(p) - 0.54) - stroke * 0.65;
    }
    else if (kind == 2)
    {
        // Android Recents (classic rounded square outline)
        float box = sdRoundedBox(p, vec2(0.50, 0.50), 0.14);
        d = abs(box) - stroke * 0.65;
    }
    else if (kind == 3)
    {
        // Close (X) icon
        vec2 a = vec2(-0.36, -0.36), b = vec2(0.36, 0.36);
        vec2 c = vec2(0.36, -0.36), e = vec2(-0.36, 0.36);
        d = min(sdSegment(p, a, b), sdSegment(p, c, e)) - stroke * 0.68;
    }
    else if (kind == 4)
    {
        // Crisp mouse cursor arrow (white fill with dark outline, tip at top-left (0.08, 0.05))
        vec2 u = localPosition / max(min(size.x, size.y), 1.0);
        // Arrow triangle + stem in normalized [0..1] space
        vec2 a = vec2(0.08, 0.05);
        vec2 b = vec2(0.08, 0.82);
        vec2 c = vec2(0.34, 0.60);
        vec2 e = vec2(0.68, 0.58);
        // Distance to two triangles: (a,b,c) and (a,c,e)
        vec2 e0 = b - a, e1 = c - b, e2 = a - c;
        vec2 p0 = u - a, p1 = u - b, p2 = u - c;
        float s0 = sign(e0.x * e2.y - e0.y * e2.x);
        vec2 pq0 = p0 - e0 * clamp(dot(p0, e0) / dot(e0, e0), 0.0, 1.0);
        vec2 pq1 = p1 - e1 * clamp(dot(p1, e1) / dot(e1, e1), 0.0, 1.0);
        vec2 pq2 = p2 - e2 * clamp(dot(p2, e2) / dot(e2, e2), 0.0, 1.0);
        float d1 = min(min(dot(pq0, pq0), dot(pq1, pq1)), dot(pq2, pq2));
        float in1 = min(min(s0 * (p0.x * e0.y - p0.y * e0.x), s0 * (p1.x * e1.y - p1.y * e1.x)), s0 * (p2.x * e2.y - p2.y * e2.x));
        float sd1 = -sqrt(d1) * sign(in1);

        vec2 f0 = c - a, f1 = e - c, f2 = a - e;
        vec2 q0 = u - a, q1 = u - c, q2 = u - e;
        float s1 = sign(f0.x * f2.y - f0.y * f2.x);
        vec2 rq0 = q0 - f0 * clamp(dot(q0, f0) / dot(f0, f0), 0.0, 1.0);
        vec2 rq1 = q1 - f1 * clamp(dot(q1, f1) / dot(f1, f1), 0.0, 1.0);
        vec2 rq2 = q2 - f2 * clamp(dot(q2, f2) / dot(f2, f2), 0.0, 1.0);
        float d2 = min(min(dot(rq0, rq0), dot(rq1, rq1)), dot(rq2, rq2));
        float in2 = min(min(s1 * (q0.x * f0.y - q0.y * f0.x), s1 * (q1.x * f1.y - q1.y * f1.x)), s1 * (q2.x * f2.y - q2.y * f2.x));
        float sd2 = -sqrt(d2) * sign(in2);

        float stem = sdSegment(u, vec2(0.32, 0.56), vec2(0.50, 0.90)) - 0.055;
        float arrow = min(min(sd1, sd2), stem);
        float aa = max(fwidth(arrow), 0.015);
        float fillCov = 1.0 - smoothstep(-aa, aa, arrow);
        float borderCov = 1.0 - smoothstep(-aa, aa, arrow - 0.065);
        vec4 borderCol = vec4(0.05, 0.07, 0.12, 0.92);
        vec4 fillCol = color;
        return mix(borderCol * borderCov, fillCol, fillCov);
    }

    float aa = max(fwidth(d), 0.02);
    float cov = 1.0 - smoothstep(-aa, aa, d);
    return color * cov;
}

void main()
{
    int mode = int(drawOptions.x + 0.5);
    vec2 sampleUv = originAndViewport.xy + textureBasis.xy * uv.x + textureBasis.zw * uv.y;
    if (mode == 0)
        pixel = color;
    else if (mode == 1 || mode == 5)
        pixel = sampleImage(sampleUv);
    else if (mode == 2 || mode == 6)
        pixel = vec4(sampleImage(sampleUv).rgb, 1.0);
    else if (mode == 3)
        pixel = vec4(wallpaper(), 1.0);
    else if (mode == 4)
        pixel = windowChrome();
    else if (mode == 7)
        pixel = roundedSolid();
    else if (mode == 8)
        pixel = roundedTexture(sampleUv);
    else
        pixel = vectorIcon();
}
