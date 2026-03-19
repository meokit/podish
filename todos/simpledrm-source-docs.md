# simpledrm Source Documents

## Kernel Documentation

- [ ] [DRM KMS Core](https://docs.kernel.org/gpu/drm-kms.html)
- [ ] [DRM KMS Helpers](https://docs.kernel.org/gpu/drm-kms-helpers.html)
- [ ] [DRM UAPI](https://docs.kernel.org/gpu/drm-uapi.html)
- [ ] [DRM Memory Management](https://docs.kernel.org/gpu/drm-mm.html)
- [ ] Search the kernel docs for `simple display pipe` and `simpledrm` references.

## libdrm Documentation

- [ ] [drm-kms(7)](https://manpages.debian.org/testing/libdrm-dev/drm-kms.7.en.html)
- [ ] `drmModeGetResources(3)`
- [ ] `drmModeGetConnector(3)`
- [ ] `drmModeGetEncoder(3)`
- [ ] `drmModeGetCrtc(3)`
- [ ] `drmModeSetCrtc(3)`
- [ ] `drmModeAddFB2(3)`
- [ ] `drmModePageFlip(3)`
- [ ] `drmModeAtomicCommit(3)`
- [ ] `drmHandleEvent(3)`

## Expected Reading Outcomes

- [ ] Identify the minimum object graph a DRM client expects.
- [ ] Identify which ioctls are mandatory for simple modeset clients.
- [ ] Identify which ioctls are mandatory for atomic clients.
- [ ] Identify how dumb buffers are created and mmaped.
- [ ] Identify how framebuffer IDs are attached to scanout objects.
- [ ] Identify how page flips and events are delivered.

## Open Questions

- [ ] Do we need EDID emulation in v1?
- [ ] Do we need atomic-first support in v1?
- [ ] Do we need a render node in v1?

