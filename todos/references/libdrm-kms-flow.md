# libdrm KMS Flow

Source:

- [drm-kms(7)](https://manpages.debian.org/testing/libdrm-dev/drm-kms.7.en.html)
- [drmModeGetResources(3)](https://manpages.debian.org/testing/libdrm-dev/drmModeGetResources.3.en.html)

## What It Says

- KMS clients typically work by discovering the device graph, choosing a mode, creating a framebuffer, and then programming a CRTC.
- The flow revolves around the same object set: connectors, encoders, CRTCs, planes, and framebuffers.
- `drmModeGetResources()` returns the device-wide object lists and size limits.
- `drmModeSetCrtc()` is the classic way to program a CRTC.
- `drmModePageFlip()` is used for synchronized flips.

## Key Implementation Takeaways

- `GETRESOURCES` must return a coherent graph.
- Connectors should lead to encoders.
- Encoders should lead to CRTCs.
- The resource counts and object IDs need to be stable enough for client-side caching.

## Why This Matters For Fiberish

- This is the flow libdrm-based tools will follow first.
- If the resource graph is incomplete, userspace will stop before any rendering happens.

## Practical Checklist

- [ ] Make `GETRESOURCES` coherent.
- [ ] Make `GETCONNECTOR` and `GETENCODER` agree with it.
- [ ] Make `SETCRTC` or `ATOMIC` accept the selected framebuffer and mode.

