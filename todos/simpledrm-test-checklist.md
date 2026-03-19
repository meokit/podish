# simpledrm Test Checklist

## VFS And Device Tests

- [ ] Confirm `/dev/dri/card0` exists after startup.
- [ ] Confirm the node is a character device.
- [ ] Confirm the node has the expected `rdev`.
- [ ] Confirm `open()` succeeds.
- [ ] Confirm unknown ioctl returns `ENOTTY`.

## DRM Query Tests

- [ ] Confirm `DRM_IOCTL_VERSION` returns stable metadata.
- [ ] Confirm `DRM_IOCTL_GET_CAP` reports dumb buffer support.
- [ ] Confirm `DRM_IOCTL_SET_CLIENT_CAP` accepts supported caps.
- [ ] Confirm `GETRESOURCES` returns one connector, one encoder, one CRTC.
- [ ] Confirm `GETCONNECTOR` reports `connected` and exposes one mode.

## Buffer And Scanout Tests

- [ ] Confirm `CREATE_DUMB` returns valid width, height, pitch, and size.
- [ ] Confirm `MAP_DUMB` returns mmap-able memory.
- [ ] Confirm `ADDFB2` succeeds for the chosen format.
- [ ] Confirm `RMFB` cleans up framebuffer state.
- [ ] Confirm the mapped buffer can be written by guest code.

## Modeset Tests

- [ ] Confirm `SETCRTC` works for the primary framebuffer path.
- [ ] If atomic is implemented, confirm `ATOMIC` commits a visible frame.
- [ ] Confirm mode changes either succeed or fail cleanly.
- [ ] Confirm buffer rebinding does not leak stale state.

## Event And Poll Tests

- [ ] Confirm page-flip events are delivered if implemented.
- [ ] Confirm `poll()` or `select()` behavior matches the file mode used.
- [ ] Confirm event handling does not deadlock under repeated commits.

## Host Presentation Tests

- [ ] Confirm a known pixel pattern reaches the host display backend.
- [ ] Confirm resize or re-mode-set updates the presentation surface.
- [ ] Confirm the backend remains thread-safe under repeated frame updates.

## Regression Tests

- [ ] Confirm close and reopen does not leak device state.
- [ ] Confirm resource enumeration stays stable across repeated calls.
- [ ] Confirm invalid object IDs fail with the expected errno.
- [ ] Confirm a missing or disabled host backend fails predictably.

