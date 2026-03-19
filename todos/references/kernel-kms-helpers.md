# Kernel KMS Helpers

Source:

- [Mode Setting Helper Functions](https://docs.kernel.org/gpu/drm-kms-helpers.html)

## What It Says

- The helper layer exists for simple display hardware.
- `drm_simple_display_pipe_init()` builds a simple pipeline with one fullscreen scanout buffer and one output.
- The helper ties together plane, CRTC, and encoder into one fixed entity.
- A separate connector can be attached when needed.

## Key Implementation Takeaways

- For a simple emulator display path, this is the clearest model to copy.
- The helper design suggests a fixed-format, fixed-output first version.
- Cleanup is handled by DRM core through `drm_mode_config_cleanup()`.

## Why This Matters For Fiberish

- Fiberish can model `simpledrm` as a minimal display pipe rather than a general GPU.
- This reduces the amount of DRM surface area needed for a working first pass.
- It also gives a natural place to connect host presentation to the guest framebuffer.

## Practical Checklist

- [ ] Model the device as a simple display pipe.
- [ ] Keep the first mode fixed.
- [ ] Support a small set of framebuffer formats first.
- [ ] Let DRM core own most teardown.

