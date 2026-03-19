# simpledrm Object Model

This document breaks the DRM/KMS surface into the objects Fiberish needs to model.

## 1. Connector

### Role

- Represents the display sink visible to userspace.
- Holds the list of supported modes.
- Reports whether the output is connected.

### Minimum v1 behavior

- [ ] Expose exactly one connector.
- [ ] Report `connected`.
- [ ] Mark one mode as preferred.
- [ ] Point the connector at one encoder.
- [ ] Keep the connector ID stable during the fd lifetime.

### Likely Fiberish state

- [ ] Connector ID
- [ ] Connection status
- [ ] Supported mode list
- [ ] Preferred mode index
- [ ] Encoder ID binding

### Relevant ioctls

- `DRM_IOCTL_MODE_GETCONNECTOR`
- `DRM_IOCTL_MODE_GETRESOURCES`

## 2. Encoder

### Role

- Bridges connector and CRTC.
- In simple setups, this is usually a fixed mapping.

### Minimum v1 behavior

- [ ] Expose exactly one encoder.
- [ ] Report the single CRTC mask.
- [ ] Bind to the single CRTC.

### Likely Fiberish state

- [ ] Encoder ID
- [ ] Type
- [ ] Possible CRTC mask
- [ ] Current CRTC ID

### Relevant ioctls

- `DRM_IOCTL_MODE_GETENCODER`
- `DRM_IOCTL_MODE_GETRESOURCES`

## 3. CRTC

### Role

- Describes the scanout engine that reads a framebuffer and drives output timing.

### Minimum v1 behavior

- [ ] Expose exactly one CRTC.
- [ ] Allow one active framebuffer.
- [ ] Carry one mode blob or one fixed mode description.
- [ ] Support legacy `SETCRTC` or atomic `ACTIVE`/`MODE_ID` programming.

### Likely Fiberish state

- [ ] CRTC ID
- [ ] Active framebuffer ID
- [ ] Current mode
- [ ] Active flag
- [ ] Connector binding

### Relevant ioctls

- `DRM_IOCTL_MODE_GETCRTC`
- `DRM_IOCTL_MODE_SETCRTC`
- `DRM_IOCTL_MODE_ATOMIC`

## 4. Plane

### Role

- Describes where framebuffer pixels are sourced from.
- For simpledrm, the primary plane is usually enough.

### Minimum v1 behavior

- [ ] Expose one primary plane.
- [ ] Support the chosen scanout format.
- [ ] Bind the plane to the single CRTC.

### Likely Fiberish state

- [ ] Plane ID
- [ ] Plane type
- [ ] Supported formats
- [ ] Attached framebuffer ID
- [ ] Source and destination rectangles

### Relevant ioctls

- `DRM_IOCTL_MODE_GETPLANERESOURCES`
- `DRM_IOCTL_MODE_GETPLANE`
- `DRM_IOCTL_MODE_SETPLANE`
- `DRM_IOCTL_MODE_ATOMIC`

## 5. Framebuffer

### Role

- Points at guest-backed pixel memory.
- Becomes the active scanout source.

### Minimum v1 behavior

- [ ] Support one linear 32-bit RGB format.
- [ ] Track width, height, pitch, handle, and ID.
- [ ] Allow attach and detach from the active CRTC or plane.

### Likely Fiberish state

- [ ] Framebuffer ID
- [ ] Buffer handle
- [ ] Width / height
- [ ] Pitch
- [ ] Pixel format
- [ ] Mapping / storage backing

### Relevant ioctls

- `DRM_IOCTL_MODE_CREATE_DUMB`
- `DRM_IOCTL_MODE_ADDFB2`
- `DRM_IOCTL_MODE_RMFB`
- `DRM_IOCTL_MODE_GETFB2`

## 6. Dumb Buffer

### Role

- User-mappable storage for software rendering.
- Often the easiest way to get pixels onto the screen.

### Minimum v1 behavior

- [ ] Allocate a linear buffer.
- [ ] Return a size and pitch that libdrm accepts.
- [ ] Provide an mmap offset.
- [ ] Keep the storage alive until the user closes or destroys it.

### Likely Fiberish state

- [ ] Handle
- [ ] Size
- [ ] Pitch
- [ ] Fake mmap offset
- [ ] Host storage object

### Relevant ioctls

- `DRM_IOCTL_MODE_CREATE_DUMB`
- `DRM_IOCTL_MODE_MAP_DUMB`
- `DRM_IOCTL_MODE_DESTROY_DUMB`

## 7. Properties

### Role

- The atomic API uses properties to update object state.

### Minimum v1 behavior

- [ ] Only add properties if atomic is enabled.
- [ ] Keep property IDs stable.
- [ ] Attach the minimum property set needed for a commit.

### Likely Fiberish state

- [ ] Property registry
- [ ] Property IDs
- [ ] Object-to-property mapping
- [ ] Blob storage for modes

### Relevant ioctls

- `DRM_IOCTL_MODE_GETPROPERTY`
- `DRM_IOCTL_MODE_SETPROPERTY`
- `DRM_IOCTL_MODE_OBJ_GETPROPERTIES`
- `DRM_IOCTL_MODE_OBJ_SETPROPERTY`
- `DRM_IOCTL_MODE_GETPROPBLOB`

## 8. Events

### Role

- Lets libdrm clients observe page flips or vblank completion.

### Minimum v1 behavior

- [ ] Decide whether to emit events immediately or on a timer.
- [ ] Make the fd readable when events are pending.
- [ ] Serialize event payloads in libdrm-compatible format.

### Likely Fiberish state

- [ ] Event queue
- [ ] Pending flip state
- [ ] Readable notification
- [ ] Event sequence counter

### Relevant ioctls

- `DRM_IOCTL_MODE_PAGE_FLIP`
- `poll()`
- `read()`

## Suggested Implementation Order

- [ ] Connector, encoder, CRTC.
- [ ] Dumb buffer and framebuffer.
- [ ] Legacy modeset.
- [ ] Atomic property system.
- [ ] Page flip and events.

