# Fiberish simpledrm / DRM / KMS Checklist

## Goal

- [ ] Add a minimal but real DRM/KMS display path for Fiberish.
- [ ] Make early Linux graphics output work through a `simpledrm`-style device.
- [ ] Keep the first version intentionally small: one display, one mode, one primary plane.

## Scope Assumptions

- [ ] Target a single primary GPU/display node, not a full GPU emulator.
- [ ] Prefer a fixed-mode scanout path before adding hotplug or multi-monitor support.
- [ ] Start with linear framebuffer formats only.
- [ ] Treat atomic KMS as the preferred long-term API, but allow a legacy fallback if that is simpler to bring up.

## Source Of Truth To Keep Nearby

- [ ] Kernel KMS core docs: `docs.kernel.org/gpu/drm-kms.html`
- [ ] Kernel KMS helper docs: `docs.kernel.org/gpu/drm-kms-helpers.html`
- [ ] Kernel DRM UAPI docs: `docs.kernel.org/gpu/drm-uapi.html`
- [ ] Kernel DRM memory management docs: `docs.kernel.org/gpu/drm-mm.html`
- [ ] libdrm KMS manpage: `drm-kms(7)`
- [ ] libdrm resource/query docs: `drmModeGetResources(3)`, `drmModeGetConnector(3)`, `drmModeSetCrtc(3)`, `drmModeAtomicCommit(3)`, `drmHandleEvent(3)`

## Phase 1: Define The Display Architecture

- [ ] Decide whether Fiberish will expose a direct `/dev/dri/card0`-style DRM primary node only, or also a render node later.
- [ ] Define the guest-visible device model as one connector, one encoder, one CRTC, one primary plane.
- [ ] Choose a default mode, including width, height, refresh rate, and pixel format.
- [ ] Decide the initial framebuffer format. Prefer `XRGB8888` first.
- [ ] Decide whether the first implementation will use legacy modesetting, atomic modesetting, or atomic with legacy shims.
- [ ] Decide whether the host presentation backend will be a GUI window, a platform surface, or an offscreen framebuffer sink.

## Phase 2: Add A Display Backend Abstraction

- [ ] Introduce a display-sink abstraction that is independent from DRM ioctls.
- [ ] Define an API for presenting a full frame to the host.
- [ ] Define an API for partial frame updates if you want dirty-rect support later.
- [ ] Define a pixel format conversion layer if the host surface format differs from guest scanout format.
- [ ] Add a way to resize or re-create the host surface if the guest mode changes.
- [ ] Decide whether scanout memory is host-owned, guest-owned, or a shared copy buffer.
- [ ] Add synchronization so guest writes do not race the host presentation thread.

## Phase 3: Add The DRM Device Node

- [ ] Create a new inode implementation for the DRM primary node.
- [ ] Register the node under `/dev/dri/card0` during device initialization.
- [ ] Choose stable major/minor handling for the device number.
- [ ] Make `open()` create or bind a display session state object.
- [ ] Make `release()` cleanly tear down the session and free resources.
- [ ] Ensure the node reports `CharDev` and sensible permissions.
- [ ] Decide whether the device should exist only when the host display backend is available.

## Phase 4: Implement Basic DRM Core Queries

- [ ] Implement `DRM_IOCTL_VERSION`.
- [ ] Implement `DRM_IOCTL_GET_CAP`.
- [ ] Support `DRM_CAP_DUMB_BUFFER`.
- [ ] Decide whether to advertise `DRM_CAP_ADDFB2_MODIFIERS`.
- [ ] Decide whether to advertise `DRM_CAP_ATOMIC`.
- [ ] Implement `DRM_IOCTL_SET_CLIENT_CAP` if atomic or universal plane support is enabled.
- [ ] Return stable driver name, version, and date strings.
- [ ] Make error handling consistent with libdrm expectations.

## Phase 5: Implement KMS Resource Enumeration

- [ ] Implement `DRM_IOCTL_MODE_GETRESOURCES`.
- [ ] Implement `DRM_IOCTL_MODE_GETCONNECTOR`.
- [ ] Implement `DRM_IOCTL_MODE_GETENCODER`.
- [ ] Implement `DRM_IOCTL_MODE_GETCRTC`.
- [ ] Make connector status report `connected`.
- [ ] Expose exactly one preferred mode at first.
- [ ] Make the connector point at the single encoder and CRTC you created.
- [ ] Keep object IDs stable for the life of the device session.
- [ ] Decide whether to expose EDID data or a synthetic EDID later.

## Phase 6: Implement Dumb Buffer And Framebuffer Paths

- [ ] Implement `DRM_IOCTL_MODE_CREATE_DUMB`.
- [ ] Implement `DRM_IOCTL_MODE_MAP_DUMB`.
- [ ] Implement `DRM_IOCTL_MODE_ADDFB2`.
- [ ] Implement `DRM_IOCTL_MODE_RMFB`.
- [ ] Optionally implement `DRM_IOCTL_MODE_GETFB2` for inspection and debugging.
- [ ] Back dumb buffers with memory that the guest can mmap.
- [ ] Ensure pitch, size, and alignment calculations match DRM expectations.
- [ ] Support at least one linear 32-bit RGB scanout format.
- [ ] Decide whether to add `RGB565` as a secondary format for compatibility.
- [ ] Track framebuffer lifetimes so buffers are not freed while still attached to a CRTC or plane.

## Phase 7: Implement Modeset Or Atomic Commit

- [ ] If using legacy KMS, implement `DRM_IOCTL_MODE_SETCRTC`.
- [ ] If using atomic KMS, implement `DRM_IOCTL_MODE_ATOMIC`.
- [ ] If using atomic KMS, implement `DRM_IOCTL_MODE_GETPLANERESOURCES`.
- [ ] If using atomic KMS, implement `DRM_IOCTL_MODE_GETPLANE`.
- [ ] If using atomic KMS, implement `DRM_IOCTL_MODE_GETPROPERTY`.
- [ ] If using atomic KMS, implement `DRM_IOCTL_MODE_SETPROPERTY`.
- [ ] Expose plane properties such as `FB_ID`, `CRTC_ID`, `SRC_X`, `SRC_Y`, `SRC_W`, `SRC_H`, `CRTC_X`, `CRTC_Y`, `CRTC_W`, `CRTC_H`.
- [ ] Expose CRTC properties such as `MODE_ID` and `ACTIVE`.
- [ ] Expose connector properties such as `CRTC_ID`.
- [ ] Validate state transitions before committing.
- [ ] Make failed commits return a clean DRM-style errno.

## Phase 8: Add Present/Flip/Event Support

- [ ] Implement `DRM_IOCTL_MODE_PAGE_FLIP` if you want page-flip-based clients.
- [ ] Implement vblank or page-flip event queuing if the client requests event delivery.
- [ ] Make the file descriptor pollable in the way libdrm expects.
- [ ] Wire the event path into `drmHandleEvent()`-style userland flows.
- [ ] Decide whether to simulate a vblank timer or complete flips immediately.
- [ ] If flips are immediate, ensure the event ordering is still sensible.

## Phase 9: Host Presentation Behavior

- [ ] Present the framebuffer to the host on every modeset or flip.
- [ ] Add a fast path for whole-frame copy first.
- [ ] Add a dirty-rect or page-granular update path later if needed.
- [ ] Add resize handling when the guest changes mode.
- [ ] Add a black-clear path for blanking or uninitialized buffers.
- [ ] Decide how to handle host window close, minimize, or unavailable display backend.
- [ ] Make display refresh thread-safe.

## Phase 10: Integration With Existing Fiberish Startup

- [ ] Hook the new display node into the same startup path that currently populates `/dev`.
- [ ] Decide whether `/dev/dri` should be created automatically or only when a display backend is configured.
- [ ] Keep `/dev/null`, `/dev/tty`, `/dev/ptmx`, and the new DRM node in the same initialization flow if that simplifies lifecycle handling.
- [ ] Make sure the DRM node is available in both overlay-root and tmpfs-root boot paths.
- [ ] Verify the display backend is created before guest userland starts probing `/dev/dri`.

## Phase 11: Kernel/Userland Compatibility Checks

- [ ] Confirm libdrm can open the node and identify it as a DRM device.
- [ ] Confirm `drmModeGetResources()` returns a sane resource graph.
- [ ] Confirm `drmModeGetConnector()` reports the connector as connected and exposes one mode.
- [ ] Confirm a dumb buffer can be created, mmaped, and filled.
- [ ] Confirm framebuffer registration succeeds.
- [ ] Confirm modeset or atomic commit activates the display.
- [ ] Confirm compositor-style probes do not fail on missing mandatory properties.

## Phase 12: Tests To Add

- [ ] Add a syscall-level test for the DRM node existing at boot.
- [ ] Add a syscall-level test for `open()` on `/dev/dri/card0`.
- [ ] Add a syscall-level test for `GET_CAP` and `VERSION`.
- [ ] Add a syscall-level test for resource enumeration.
- [ ] Add a syscall-level test for dumb buffer creation and mmap.
- [ ] Add a syscall-level test for framebuffer creation.
- [ ] Add a syscall-level test for modeset or atomic commit.
- [ ] Add a host-side rendering test that writes a known pattern and verifies the host backend received it.
- [ ] Add a regression test for unknown ioctl returning `ENOTTY`.
- [ ] Add a regression test for cleanup after fd close.
- [ ] Add a regression test for mode switch or buffer rebind if that is in scope.

## Phase 13: Debugging And Diagnostics

- [ ] Add tracing around DRM ioctl entry and exit.
- [ ] Add logging for connector and mode enumeration.
- [ ] Add logging for framebuffer creation and presentation.
- [ ] Add logging for commit failures with enough state to diagnose bad object IDs or bad properties.
- [ ] Add a debug flag to dump framebuffer contents or metadata.
- [ ] Add a way to assert which thread owns the display backend.

## Phase 14: Nice-To-Have Follow-Ups

- [ ] Add `PRIME` import/export only if you later need buffer sharing.
- [ ] Add a render node only if you later need a separation between display and rendering clients.
- [ ] Add cursor plane support only after the primary plane path is stable.
- [ ] Add modifiers only if a client actually needs them.
- [ ] Add EDID emulation only if userland needs richer monitor identity data.
- [ ] Add hotplug behavior only if you plan to simulate display attach/detach.

## Done Criteria

- [ ] A Linux guest can discover `/dev/dri/card0`.
- [ ] A libdrm-based client can enumerate the device and select a mode.
- [ ] A dumb framebuffer can be created and mapped.
- [ ] A modeset or atomic commit makes the host display show pixels.
- [ ] The implementation has tests that cover the path end to end.

## Open Questions

- [ ] Do you want to implement legacy KMS first and add atomic later, or go atomic-first?
- [ ] Do you want a native host window backend, or should the first version render into an offscreen surface?
- [ ] Do you want the checklist to be converted into GitHub issue-sized tasks next?
