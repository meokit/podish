# simpledrm Interface Checklist

## Device Model

- [ ] Expose one primary DRM node at `/dev/dri/card0`.
- [ ] Keep the first version to one connector, one encoder, one CRTC, and one primary plane.
- [ ] Choose a fixed default mode.
- [ ] Choose a fixed scanout pixel format, preferably `XRGB8888`.

## Core DRM Queries

- [ ] Implement `DRM_IOCTL_VERSION`.
- [ ] Implement `DRM_IOCTL_GET_CAP`.
- [ ] Implement `DRM_IOCTL_SET_CLIENT_CAP`.
- [ ] Advertise `DRM_CAP_DUMB_BUFFER`.
- [ ] Decide whether to advertise `DRM_CAP_ATOMIC`.
- [ ] Decide whether to advertise `DRM_CAP_ADDFB2_MODIFIERS`.

## Resource Enumeration

- [ ] Implement `DRM_IOCTL_MODE_GETRESOURCES`.
- [ ] Implement `DRM_IOCTL_MODE_GETCONNECTOR`.
- [ ] Implement `DRM_IOCTL_MODE_GETENCODER`.
- [ ] Implement `DRM_IOCTL_MODE_GETCRTC`.
- [ ] If atomic is enabled, implement `DRM_IOCTL_MODE_GETPLANERESOURCES`.
- [ ] If atomic is enabled, implement `DRM_IOCTL_MODE_GETPLANE`.
- [ ] If atomic is enabled, implement `DRM_IOCTL_MODE_GETPROPERTY`.
- [ ] If atomic is enabled, implement `DRM_IOCTL_MODE_SETPROPERTY`.

## Dumb Buffers And Framebuffers

- [ ] Implement `DRM_IOCTL_MODE_CREATE_DUMB`.
- [ ] Implement `DRM_IOCTL_MODE_MAP_DUMB`.
- [ ] Implement `DRM_IOCTL_MODE_ADDFB2`.
- [ ] Implement `DRM_IOCTL_MODE_RMFB`.
- [ ] Optionally implement `DRM_IOCTL_MODE_GETFB2`.
- [ ] Ensure pitch, size, and alignment match libdrm expectations.
- [ ] Back scanout memory with guest-mappable storage.

## Modeset And Commit

- [ ] Implement `DRM_IOCTL_MODE_SETCRTC` if using legacy modeset.
- [ ] Implement `DRM_IOCTL_MODE_ATOMIC` if using atomic modeset.
- [ ] Expose connector, CRTC, and plane properties needed by atomic clients.
- [ ] Validate object IDs before commit.
- [ ] Return stable DRM-style errnos on failure.

## Events And Presentation

- [ ] Implement `DRM_IOCTL_MODE_PAGE_FLIP` if page-flip clients are in scope.
- [ ] Support vblank or flip-complete event delivery if requested.
- [ ] Make the fd pollable in a way that matches libdrm expectations.
- [ ] Present the framebuffer to the host after modeset or flip.

## Integration Points In Fiberish

- [ ] Add the DRM node to the existing `/dev` initialization path.
- [ ] Decide whether the node is always created or only when the host display backend is present.
- [ ] Make device lifetime follow inode and fd lifetime cleanly.
- [ ] Add logging around ioctl entry, object creation, and presentation.

