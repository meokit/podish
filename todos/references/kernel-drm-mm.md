# Kernel DRM Memory Management

Source:

- [DRM Memory Management](https://docs.kernel.org/gpu/drm-mm.html)

## What It Says

- GEM and TTM are the main DRM memory managers.
- For GEM-backed memory, userspace mmap works through a fake offset.
- The driver allocates that offset with `drm_gem_create_mmap_offset()`.
- Userspace then calls `mmap()` with that offset.
- `drm_gem_mmap()` looks up the object and wires the VMA.

## Key Implementation Takeaways

- Dumb buffers typically need a GEM-style backing object or an equivalent mmapable storage layer.
- The mmap path is part of the user contract, not an optional extra.
- The fake offset mechanism is important for compatibility with libdrm expectations.

## Why This Matters For Fiberish

- Fiberish needs a buffer story that works with guest-side mmap.
- If you expose dumb buffers, you need a stable mapping mechanism and clear lifetime rules.
- This is likely the core of the implementation for a `simpledrm`-style framebuffer device.

## Practical Checklist

- [ ] Pick a backing store for scanout buffers.
- [ ] Decide whether to model it as GEM-like objects or an emulator-specific equivalent.
- [ ] Expose mmap-compatible offsets.
- [ ] Ensure unmap / close / framebuffer removal clean up correctly.

