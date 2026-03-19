# Kernel DRM UAPI

Source:

- [Userland interfaces](https://docs.kernel.org/gpu/drm-uapi.html)

## What It Says

- DRM exposes capabilities and ioctls to userspace.
- If a driver supports dumb buffers, it advertises `DRM_CAP_DUMB_BUFFER`.
- If a driver supports modifiers in `ADDFB2`, it advertises `DRM_CAP_ADDFB2_MODIFIERS`.
- If a driver supports atomic mode-setting, userspace can enable `DRM_CLIENT_CAP_ATOMIC`.
- Enabling atomic implicitly enables universal planes.

## Dumb Buffer Path

- Userspace creates a dumb buffer with `DRM_IOCTL_MODE_CREATE_DUMB`.
- Userspace maps it with `DRM_IOCTL_MODE_MAP_DUMB`.
- Userspace registers it as a framebuffer with `DRM_IOCTL_MODE_ADDFB2`.
- The docs describe this as a primitive scanout buffer path suitable for software rendering.

## Atomic And Legacy Notes

- Atomic clients need `DRM_CLIENT_CAP_ATOMIC`.
- `DRM_CLIENT_CAP_UNIVERSAL_PLANES` exposes all planes to userspace.
- `DRM_IOCTL_MODE_ATOMIC` is the modern commit path.
- `DRM_IOCTL_MODE_SETCRTC` remains the classic legacy modeset path.

## Event Notes

- Page flip and vblank events are part of normal DRM client flows.
- `DRM_MODE_PAGE_FLIP_TARGET_*` requires `DRM_CAP_PAGE_FLIP_TARGET`.
- `DRM_EVENT_VBLANK` and `DRM_EVENT_FLIP_COMPLETE` are the event types to keep in mind for polling and `drmHandleEvent()`.

## Why This Matters For Fiberish

- This doc defines the core userland contract Fiberish needs to satisfy.
- If a tool like libdrm cannot enumerate resources, create dumb buffers, and commit a mode, the display path will not be usable.

## Practical Checklist

- [ ] Return `DRM_CAP_DUMB_BUFFER`.
- [ ] Decide on `DRM_CLIENT_CAP_ATOMIC`.
- [ ] Implement `GETRESOURCES`, `GETCONNECTOR`, `GETCRTC`, `CREATE_DUMB`, `MAP_DUMB`, `ADDFB2`.
- [ ] Add `ATOMIC` or `SETCRTC` depending on chosen path.

