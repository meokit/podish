layout(std140, set = 0, binding = 0) uniform Parameters
{
    vec4 rect;
    vec4 color;
    vec4 titleColor;
    vec4 originAndViewport;
    vec4 textureBasis;
    vec4 chrome;
    vec4 drawOptions;
    uvec4 source;
};
