# simpledrm Fiberish Touchpoints

This document maps the likely code changes in Fiberish to the DRM pieces they support.

## Device Registration

- [ ] Add a DRM display inode class.
- [ ] Register the node during `SyscallManager.MountStandardDev()`.
- [ ] Decide if `/dev/dri` should be created alongside `null`, `tty`, and `ptmx`.

Likely files:

- `Fiberish.Core/Syscalls/SyscallManager.cs`
- `Fiberish.Core/VFS/Devices.cs`
- `Fiberish.Core/VFS/Structs.cs`

## Ioctl Dispatch

- [ ] Extend inode `Ioctl()` handling for the DRM node.
- [ ] Add request decoding for DRM ioctls.
- [ ] Add stable logging for command, object ID, and return value.

Likely files:

- `Fiberish.Core/Syscalls/SyscallHandlers.Ioctl.cs`
- `Fiberish.Core/VFS/Structs.cs`

## DRM Session State

- [ ] Add a per-open session object.
- [ ] Track resource IDs and object lifetimes in the session.
- [ ] Keep framebuffer references tied to active scanout objects.

Likely files:

- new `Fiberish.Core/VFS/DRM/DrmSession.cs`
- new `Fiberish.Core/VFS/DRM/DrmObjectRegistry.cs`

## Memory And Scanout

- [ ] Add dumb buffer storage.
- [ ] Add mmap offset bookkeeping.
- [ ] Add framebuffer to host-surface presentation.

Likely files:

- new `Fiberish.Core/VFS/DRM/DrmBuffer.cs`
- new `Fiberish.Core/VFS/DRM/DrmFramebuffer.cs`
- `Fiberish.Core/Memory/*`

## Display Backend

- [ ] Add a host rendering sink.
- [ ] Add frame copy and resize logic.
- [ ] Add a way to notify the host backend when the scanout changes.

Likely files:

- new `Fiberish.Core/VFS/DRM/DisplayBackend.cs`
- new `Fiberish.Core/VFS/DRM/DisplaySurface.cs`

## Tests

- [ ] Add VFS tests for `/dev/dri/card0`.
- [ ] Add syscall tests for DRM queries and dumb buffers.
- [ ] Add end-to-end tests for scanout and presentation.

Likely files:

- `Fiberish.Tests/Syscalls/*`
- `Fiberish.Tests/VFS/*`

