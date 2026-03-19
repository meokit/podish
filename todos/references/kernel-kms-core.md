# Kernel KMS Core

Source:

- [Kernel Mode Setting (KMS)](https://docs.kernel.org/gpu/drm-kms.html)

## What It Says

- KMS is built around a small object graph.
- The main objects are framebuffers, planes, CRTCs, encoders, and connectors.
- A framebuffer feeds a plane.
- A plane feeds a CRTC.
- A CRTC feeds an encoder and connector path to a display sink.

## Key Implementation Takeaways

- Initialize mode-setting core with `drmm_mode_config_init()`.
- Set min/max framebuffer dimensions in mode config.
- Fill in `struct drm_mode_config_funcs`.
- Implement framebuffer creation via `fb_create` if your driver manages its own buffer objects.
- Implement atomic callbacks if you want atomic mode-setting support.

## Why This Matters For Fiberish

- Fiberish does not need a full GPU stack to start.
- A simple device can expose just one fixed display pipeline.
- The kernel docs explicitly describe the KMS object model in a way that maps well to a `simpledrm`-style implementation.

## Practical Checklist

- [ ] Define one framebuffer path.
- [ ] Define one plane.
- [ ] Define one CRTC.
- [ ] Define one connector.
- [ ] Decide whether to use legacy or atomic configuration first.

