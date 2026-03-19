# simpledrm ioctl to Structs

This document lists the main DRM ioctls and the userland structs they typically carry.

## Discovery

- [ ] `DRM_IOCTL_VERSION` -> `drm_version`
- [ ] `DRM_IOCTL_GET_CAP` -> `drm_get_cap`
- [ ] `DRM_IOCTL_SET_CLIENT_CAP` -> `drm_set_client_cap`

## Resource Enumeration

- [ ] `DRM_IOCTL_MODE_GETRESOURCES` -> `drm_mode_card_res`
- [ ] `DRM_IOCTL_MODE_GETCONNECTOR` -> `drm_mode_get_connector`
- [ ] `DRM_IOCTL_MODE_GETENCODER` -> `drm_mode_get_encoder`
- [ ] `DRM_IOCTL_MODE_GETCRTC` -> `drm_mode_crtc`
- [ ] `DRM_IOCTL_MODE_GETPLANERESOURCES` -> `drm_mode_get_plane_res`
- [ ] `DRM_IOCTL_MODE_GETPLANE` -> `drm_mode_get_plane`
- [ ] `DRM_IOCTL_MODE_GETPROPERTY` -> `drm_mode_get_property`
- [ ] `DRM_IOCTL_MODE_GETPROPBLOB` -> `drm_mode_get_blob`
- [ ] `DRM_IOCTL_MODE_OBJ_GETPROPERTIES` -> `drm_mode_obj_get_properties`

## Buffers And Framebuffers

- [ ] `DRM_IOCTL_MODE_CREATE_DUMB` -> `drm_mode_create_dumb`
- [ ] `DRM_IOCTL_MODE_MAP_DUMB` -> `drm_mode_map_dumb`
- [ ] `DRM_IOCTL_MODE_DESTROY_DUMB` -> `drm_mode_destroy_dumb`
- [ ] `DRM_IOCTL_MODE_ADDFB2` -> `drm_mode_fb_cmd2`
- [ ] `DRM_IOCTL_MODE_RMFB` -> framebuffer ID
- [ ] `DRM_IOCTL_MODE_GETFB2` -> `drm_mode_fb_cmd2`

## Legacy Modeset

- [ ] `DRM_IOCTL_MODE_SETCRTC` -> `drm_mode_crtc` and `drm_mode_modeinfo`
- [ ] `DRM_IOCTL_MODE_SETPLANE` -> `drm_mode_set_plane`
- [ ] `DRM_IOCTL_MODE_CURSOR` -> `drm_mode_cursor`
- [ ] `DRM_IOCTL_MODE_PAGE_FLIP` -> `drm_mode_crtc_page_flip`

## Atomic

- [ ] `DRM_IOCTL_MODE_ATOMIC` -> `drm_mode_atomic`
- [ ] `DRM_IOCTL_MODE_ATOMIC` property payload -> property ID/value arrays
- [ ] `DRM_IOCTL_MODE_OBJ_SETPROPERTY` -> object ID, property ID, property value

## Event Handling

- [ ] `DRM_EVENT_VBLANK` -> event payload
- [ ] `DRM_EVENT_FLIP_COMPLETE` -> event payload

## Practical Notes

- [ ] Keep the struct layout exactly aligned with the kernel UAPI.
- [ ] Be careful with 32-bit guest expectations.
- [ ] For the first pass, support only the subset required by the chosen bring-up path.

