# simpledrm Implementation Order

## Step 1: Wire The Device

- [ ] Add the DRM inode type.
- [ ] Register `/dev/dri/card0`.
- [ ] Make open/close/release stateful.

## Step 2: Make Queries Work

- [ ] Implement version and capability ioctls.
- [ ] Implement resource enumeration.
- [ ] Return one fixed connector and one fixed mode.

## Step 3: Make Buffers Work

- [ ] Implement dumb buffer creation.
- [ ] Implement mmap for dumb buffers.
- [ ] Implement framebuffer registration.

## Step 4: Make Scanout Work

- [ ] Implement legacy modeset or atomic commit.
- [ ] Connect framebuffer memory to the host display sink.
- [ ] Refresh the host display after commit.

## Step 5: Make It Robust

- [ ] Add events and page flips if needed.
- [ ] Add logging and diagnostics.
- [ ] Add tests for the whole flow.

## Step 6: Expand Only If Needed

- [ ] Add atomic properties if legacy is not enough.
- [ ] Add additional formats only if a client needs them.
- [ ] Add cursor plane support only after primary scanout is stable.
- [ ] Add EDID or hotplug only if the guest stack needs them.

