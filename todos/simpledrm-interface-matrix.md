# simpledrm Interface Matrix

This document maps the DRM/KMS userland surface to likely Fiberish implementation points.

## Reading Rule

- `ioctl / cap` describes the user-facing contract.
- `uapi structs` are the kernel-visible payloads.
- `purpose` explains why the interface exists.
- `fiberish likely touchpoints` points to the files most likely to change.

## Core Device Discovery

| ioctl / cap | uapi structs | purpose | fiberish likely touchpoints |
|---|---|---|---|
| `DRM_IOCTL_VERSION` | `drm_version` | Identify driver name, version, and date | `Fiberish.Core/VFS/Devices.cs`, new `Fiberish.Core/VFS/DRM/*.cs`, `Fiberish.Core/Syscalls/SyscallHandlers.Ioctl.cs` |
| `DRM_IOCTL_GET_CAP` | `drm_get_cap` | Advertise device capabilities | same as above |
| `DRM_IOCTL_SET_CLIENT_CAP` | `drm_set_client_cap` | Let userspace enable atomic or universal-plane paths | same as above |
| `DRM_CAP_DUMB_BUFFER` | capability flag | Tell libdrm that dumb buffers are supported | device/session state + ioctl handler |
| `DRM_CAP_ATOMIC` | capability flag | Tell userspace atomic commits are supported | device/session state + ioctl handler |
| `DRM_CAP_ADDFB2_MODIFIERS` | capability flag | Tell userspace whether modifiers are accepted | device/session state + framebuffer path |

## Resource Graph

| ioctl / cap | uapi structs | purpose | fiberish likely touchpoints |
|---|---|---|---|
| `DRM_IOCTL_MODE_GETRESOURCES` | `drm_mode_card_res` | Enumerate connector / encoder / CRTC / framebuffer IDs | new DRM inode, resource model classes, ioctl handler |
| `DRM_IOCTL_MODE_GETCONNECTOR` | `drm_mode_get_connector` | Describe connector status, modes, and encoder links | connector model, mode database, ioctl handler |
| `DRM_IOCTL_MODE_GETENCODER` | `drm_mode_get_encoder` | Describe encoder-to-CRTC mapping | encoder model, ioctl handler |
| `DRM_IOCTL_MODE_GETCRTC` | `drm_mode_crtc` | Query the active CRTC state | CRTC model, ioctl handler |
| `DRM_IOCTL_MODE_GETPLANERESOURCES` | `drm_mode_get_plane_res` | Enumerate planes for atomic clients | plane model, atomic path |
| `DRM_IOCTL_MODE_GETPLANE` | `drm_mode_get_plane` | Describe plane properties and supported formats | plane model, atomic path |
| `DRM_IOCTL_MODE_GETPROPERTY` | `drm_mode_get_property` | Expose object properties to atomic clients | property registry, atomic path |
| `DRM_IOCTL_MODE_GETPROPBLOB` | `drm_mode_get_blob` | Fetch mode/property blob contents | mode blob store, atomic path |
| `DRM_IOCTL_MODE_OBJ_GETPROPERTIES` | `drm_mode_obj_get_properties` | List properties attached to an object | property registry, atomic path |
| `DRM_IOCTL_MODE_OBJ_SETPROPERTY` | `drm_mode_obj_set_property` | Update a single property value | property registry, atomic path |

## Dumb Buffer Lifecycle

| ioctl / cap | uapi structs | purpose | fiberish likely touchpoints |
|---|---|---|---|
| `DRM_IOCTL_MODE_CREATE_DUMB` | `drm_mode_create_dumb` | Allocate a scanout-capable linear buffer | buffer allocator, session state, mmap backing |
| `DRM_IOCTL_MODE_MAP_DUMB` | `drm_mode_map_dumb` | Convert dumb buffer handle to mmap offset | buffer object registry, mmap offset table |
| `DRM_IOCTL_MODE_DESTROY_DUMB` | `drm_mode_destroy_dumb` | Release a dumb buffer | buffer object lifetime management |
| `DRM_IOCTL_MODE_ADDFB2` | `drm_mode_fb_cmd2` | Register a framebuffer from one or more GEM/dumb handles | framebuffer registry, format validation |
| `DRM_IOCTL_MODE_RMFB` | framebuffer ID | Remove a framebuffer from the device | framebuffer registry, reference tracking |
| `DRM_IOCTL_MODE_GETFB2` | `drm_mode_fb_cmd2` | Inspect framebuffer metadata | framebuffer registry, optional debug path |

## Modeset And Commit

| ioctl / cap | uapi structs | purpose | fiberish likely touchpoints |
|---|---|---|---|
| `DRM_IOCTL_MODE_SETCRTC` | `drm_mode_crtc` / `drm_mode_modeinfo` | Legacy modeset path for setting scanout | CRTC model, connector model, scanout backend |
| `DRM_IOCTL_MODE_ATOMIC` | `drm_mode_atomic` | Modern atomic commit path | object property system, validation, present path |
| `DRM_IOCTL_MODE_PAGE_FLIP` | `drm_mode_crtc_page_flip` | Queue a flip to a new framebuffer | CRTC model, event queue, present backend |
| `DRM_IOCTL_MODE_SETPLANE` | `drm_mode_set_plane` | Legacy plane programming | plane model, optional if legacy fallback is used |
| `DRM_IOCTL_MODE_CURSOR` | `drm_mode_cursor` | Cursor plane update on legacy paths | optional, later milestone |

## Present / Event Path

| ioctl / cap | uapi structs | purpose | fiberish likely touchpoints |
|---|---|---|---|
| `DRM_EVENT_VBLANK` | event payload | Notify vblank completion | event queue, poll/read integration |
| `DRM_EVENT_FLIP_COMPLETE` | event payload | Notify page-flip completion | event queue, poll/read integration |
| `poll()` readability | n/a | Tell libdrm there is an event to consume | inode poll logic, event queue wakeups |
| `drmHandleEvent()` compatibility | libdrm callback context | Dispatch events to userspace callbacks | event serialization, fd read path |

## Memory Mapping / Backing Store

| ioctl / cap | uapi structs | purpose | fiberish likely touchpoints |
|---|---|---|---|
| `mmap()` on dumb BO | VMA offset from `MAP_DUMB` | Let userspace write pixels directly | VM/mmap bridge, buffer object store |
| GEM-like fake offset lookup | internal DRM object state | Resolve user VMA to buffer object | buffer registry, page fault / mmap hook |
| `fbdev` emulation path | optional DRM helper behavior | Support software stacks expecting fb-like behavior | optional later integration |

## Suggested Fiberish File Boundaries

| area | likely files |
|---|---|
| device node / inode | `Fiberish.Core/VFS/Devices.cs`, new `Fiberish.Core/VFS/DRM/*` |
| ioctl dispatch | `Fiberish.Core/Syscalls/SyscallHandlers.Ioctl.cs` |
| startup registration | `Fiberish.Core/Syscalls/SyscallManager.cs` |
| Linux constants / ioctl codes | `Fiberish.Core/Native/LinuxConstants.cs` |
| tests | `Fiberish.Tests/Syscalls/*`, new `Fiberish.Tests/VFS/DRM/*` |

## Recommended First Pass

- [ ] Implement `VERSION`, `GET_CAP`, `SET_CLIENT_CAP`.
- [ ] Implement `GETRESOURCES`, `GETCONNECTOR`, `GETENCODER`, `GETCRTC`.
- [ ] Implement `CREATE_DUMB`, `MAP_DUMB`, `ADDFB2`, `RMFB`.
- [ ] Implement `SETCRTC` first if you want a minimal bring-up.
- [ ] Add atomic later once the fixed-mode path works.

## Recommended Second Pass

- [ ] Add atomic object/property plumbing.
- [ ] Add `PAGE_FLIP` and event delivery.
- [ ] Add optional plane and property enumeration.
- [ ] Add extras like modifiers, cursor plane, and EDID once the basics are stable.

