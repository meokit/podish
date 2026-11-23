namespace Podish.Cli.Display;

internal sealed partial class AngleDisplayRenderer
{
    private const string VertexShader = """
        #version 300 es
        precision highp float;

        uniform vec2 viewport;
        uniform vec4 destination;
        out vec2 uv;
        out vec2 localPosition;
        flat out vec2 size;

        void main()
        {
            uv = vec2(float(gl_VertexID & 1), float(gl_VertexID >> 1));
            size = destination.zw;
            localPosition = uv * size;
            vec2 p = (destination.xy + localPosition) / viewport;
            gl_Position = vec4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
        }
        """;

    private const string FragmentShader = """
        #version 300 es
        precision highp float;

        uniform sampler2D image;
        uniform int mode;
        uniform vec4 color;
        uniform vec4 titleColor;
        uniform vec4 chrome;
        uniform vec2 textureOrigin;
        uniform vec4 textureBasis;
        in vec2 uv;
        in vec2 localPosition;
        flat in vec2 size;
        out vec4 pixel;

        vec3 wallpaper()
        {
            vec2 p = uv - vec2(0.5);
            p.x *= size.x / size.y;

            vec3 base = vec3(0.046, 0.056, 0.089);
            float blue = exp(-3.6 * dot(p - vec2(-0.36, -0.30), p - vec2(-0.36, -0.30)));
            float violet = exp(-4.5 * dot(p - vec2(0.71, 0.18), p - vec2(0.71, 0.18)));
            base += blue * vec3(0.026, 0.072, 0.116) + violet * vec3(0.101, 0.048, 0.101);

            float curve = p.y + 0.08 + 0.17 * sin(p.x * 1.75) + 0.23 * p.x;
            float glow = exp(-7.0 * abs(curve));
            float edge = exp(-58.0 * abs(curve));
            float fade = exp(-0.75 * p.x * p.x);
            base += fade * (glow * vec3(0.013, 0.039, 0.067) + edge * vec3(0.018, 0.040, 0.067));

            float grain = fract(52.9829189 * fract(dot(floor(gl_FragCoord.xy), vec2(0.06711056, 0.00583715))));
            return base * (1.0 - 0.25 * smoothstep(0.30, 1.10, length(p))) + (grain - 0.5) / 255.0;
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

        void main()
        {
            vec2 sampleUv = textureOrigin + textureBasis.xy * uv.x + textureBasis.zw * uv.y;
            if (mode == 0)
                pixel = color;
            else if (mode == 1)
                pixel = texture(image, sampleUv).bgra;
            else if (mode == 2)
                pixel = vec4(texture(image, sampleUv).bgr, 1.0);
            else if (mode == 3)
                pixel = vec4(wallpaper(), 1.0);
            else if (mode == 4)
                pixel = windowChrome();
            else if (mode == 5)
                pixel = texture(image, sampleUv);
            else
                pixel = vec4(texture(image, sampleUv).rgb, 1.0);
        }
        """;
}
