# ANGLE-backed EGL and OpenGL ES

Podish CLI can expose EGL and OpenGL ES 2.0 to IA-32 Linux containers. Enable it with `run --gles` and choose `--fbdev` or `--wayland-server` for visible output. A container with neither display option can use offscreen pbuffers.

## Build and launch

From the repository root, build the IA-32 libraries with Zig and point Podish at an ANGLE build for the **host** OS and CPU.

```sh
graphics/build-guest.sh
export PODISH_ANGLE_DIR=/path/to/host/angle/libraries
dotnet build Podish.Cli -c Release
```

An Electron distribution is one way to obtain matching host ANGLE libraries. Use `libEGL.dll` and `libGLESv2.dll` on Windows, `libEGL.so` and `libGLESv2.so` on Linux, or the matching `.dylib` files from Electron's `Libraries` directory on macOS. Podish selects ANGLE's Metal backend on macOS, Vulkan for KMS window output on Windows and Linux, and ANGLE's default backend for other Windows and Linux EGL uses. The guest build targets glibc 2.11 or later. Set `PODISH_GLES_GUEST_DIR` if you build the guest libraries outside `artifacts/podish-gles/guest` or launch Podish from another working directory. [ANGLE build instructions](https://chromium.googlesource.com/angle/angle/+/main/doc/DevSetup.md)

```sh
cli=Podish.Cli/bin/Release/net10.0/Podish.Cli.dll
dotnet "$cli" run --rm --gles --fbdev --fbdev-size 640x480 --rootfs "$rootfs" -- /path/to/fbdev-egl-app
dotnet "$cli" run --rm --gles --wayland-server --rootfs "$rootfs" -- /path/to/wayland-egl-app
```

On startup Podish read-only bind mounts the guest EGL and GLES libraries at `/run/podish-gles` and prepends that directory to the guest's `LD_LIBRARY_PATH`. The ELF libraries issue versioned ioctls to `/dev/podish-gles`. The emulator copies pointer arguments through guest memory and keeps ANGLE contexts, surfaces and native pointers in the host process.

For fbdev, create an EGL window surface on `EGL_DEFAULT_DISPLAY`. Podish reads back the ANGLE frame into the visible framebuffer using its current depth and offsets. Clients designed for SDL2's `offscreen` backend can set the guest variable `PODISH_EGL_FBDEV_PBUFFER=1` to scale their pbuffer frames into `/dev/fb0`. That SDL2 backend provides rendering only. Native fbdev apps can use the existing `/dev/tty1` and `/dev/input/mice` input paths.

For Wayland, pass the guest `wl_display` and `wl_egl_window` to EGL. The client uses `libwayland-client.so.0` with `wl_proxy_marshal_flags` (Wayland 1.20+) and the version 3 `wl_egl_window` layout. Swaps commit double-buffered XRGB8888 `wl_shm` buffers to the existing compositor, which releases each buffer for reuse. Input and window management use the same Wayland protocols as other clients.

The guest exposes the GLES 2.0 core API, one RGBA8/D24/S8 config, EGL window surfaces and pbuffers. Swaps use CPU readback. The shaders and GLES rendering commands run on the configured ANGLE backend.

## Smoke tests

Use an IA-32 glibc root filesystem. With the libraries built, install and run the direct framebuffer probe.

```sh
tests/gles/install-probe.sh "$rootfs"
SDL_VIDEODRIVER=dummy dotnet "$cli" run --rm --gles --fbdev --fbdev-size 96x64 \
    --rootfs "$rootfs" -- /usr/local/bin/podish-gles-fbdev-probe
SDL_VIDEODRIVER=dummy dotnet "$cli" run --rm --gles --fbdev --fbdev-size 96x64 \
    --rootfs "$rootfs" -e PODISH_TEST_RGB565=1 -- /usr/local/bin/podish-gles-fbdev-probe
```

The probe compiles shaders, draws with client-side arrays and an element buffer, checks pixel readback, and verifies the color and orientation of pixels read from `/dev/fb0`.

For the game tests, install the i386 `chocolate-doom` and `supertux` packages and their dependencies in a Debian 12 rootfs. Copy `doom1.wad` from the [classic games rootfs](../tests/fbdev-games/README.md) to `/usr/share/games/doom/doom1.wad` in that rootfs.

```sh
tests/gles/install-probe.sh "$rootfs"
SDL_VIDEODRIVER=dummy tests/gles/run.sh "$rootfs" fbdev doom
SDL_VIDEODRIVER=dummy tests/gles/run.sh "$rootfs" fbdev supertux
tests/gles/run.sh "$rootfs" wayland doom
tests/gles/run.sh "$rootfs" wayland supertux
```

The game probe selects SDL2's `opengles2` backend, checks for a nonuniform frame and exits. It also sets the GLES profile before SDL2 2.26 creates a Wayland window. The fbdev test reads the rendered game image from `/dev/fb0`. The Wayland launcher checks the compositor log for an XRGB8888 buffer and needs a host SDL video driver that supports the Wayland compositor's SDL window.

## Virtual KMS and GBM

`--kmsdrm` exposes `/dev/dri/card0` and `/dev/dri/renderD128` and mounts the guest `libgbm.so.1`. The virtual card has one 640×480 output with legacy modesetting, a universal primary plane, CPU-backed linear BOs, and vblank-timed page-flip events. The distribution's libdrm can discover both nodes and use PRIME to share linear buffers between open file descriptions. The render node can allocate and import buffers without acquiring DRM master.

Combine `--kmsdrm --gles` to open a host SDL window and enable GPU-backed GBM surfaces. A GBM surface has three texture-bindable EGL pbuffers. Guest EGL swap publishes a finished BO, then KMS modeset or flip selects the BO to present. The host ANGLE context samples it into the window using `eglBindTexImage`. The virtual flip event follows the GPU fence. Frame pixels stay in ANGLE during this path.

The macOS Metal path is tested with Chocolate Doom and SuperTux. The Windows and Linux KMS path requires a Vulkan-enabled ANGLE build, a Vulkan driver, and a real SDL window. SDL supplies Win32 or X11 window handles; a native Wayland window also requires both SDL and ANGLE to be built with Wayland support. The window and pbuffer config must support binding the pbuffer to a texture, and ANGLE must support EGL fence sync. See [the cross-platform media probes](../tests/media/README.md) for setup and SuperTuxKart.

Use an IA-32 glibc rootfs with `libdrm2`, `libdrm-dev` and `libgbm-dev` to build the smoke tests. The development headers can also be provided with the host variables `DRM_HEADERS` and `GBM_HEADERS`.

```sh
graphics/build-guest.sh
tests/kmsdrm/install-probe.sh "$rootfs"
dotnet "$cli" run --rm --kmsdrm --rootfs "$rootfs" -- /usr/local/bin/podish-kmsdrm-probe
dotnet "$cli" run --rm --kmsdrm --gles --rootfs "$rootfs" -- /usr/local/bin/podish-kmsdrm-egl-probe
```

The GPU probe checks that EGL swap leaves the active KMS framebuffer alone. It then verifies modeset, page flip, the active BO's lifetime after the GBM lock is released, three-slot pool reuse and clean teardown.

### Run a DRM Wayland compositor

Debian Bookworm i386 Cage 0.1.4 and wlroots 0.15.1 can run directly on the virtual DRM card. Install Cage, Xwayland, Weston, and Mesa's EGL, GLES and GBM runtime packages in the guest. Run Cage as a non-root guest user with a writable `XDG_RUNTIME_DIR` and `/tmp/.X11-unix`. The command below uses `/run/user/1000` for the guest runtime directory.

```sh
tests/kmsdrm/install-probe.sh "$rootfs"
dotnet "$cli" run --rm --kmsdrm --fbdev --rootfs "$rootfs" --user 1000:1000 \
    -e XDG_RUNTIME_DIR=/run/user/1000 -e LIBSEAT_BACKEND=builtin -e SEATD_VTBOUND=0 \
    -e WLR_BACKENDS=drm -e WLR_DRM_DEVICES=/dev/dri/card0 -e WLR_RENDERER=gles2 \
    -e WLR_RENDERER_ALLOW_SOFTWARE=1 -e GBM_ALWAYS_SOFTWARE=1 \
    -e MESA_LOADER_DRIVER_OVERRIDE=kms_swrast \
    -e LD_PRELOAD=/usr/lib/i386-linux-gnu/libgbm.so.1 \
    -- /usr/bin/cage -d -- /usr/local/bin/podish-kmsdrm-compositor-probe /usr/bin/weston-simple-shm
```

The probe reports eight changing frames from the KMS scanout preview. Mesa's software DRI driver allocates GBM buffers on `renderD128`, and wlroots imports them on `card0` for scanout. Use `--static /usr/bin/weston-simple-dmabuf-feedback` in place of `/usr/bin/weston-simple-shm` to verify a client that sends its GBM buffers over `zwp_linux_dmabuf_v1`.

To exercise Pixman, set `WLR_RENDERER=pixman` and omit the Mesa variables and preload. For either software renderer, `--fbdev` displays the CPU-backed KMS scanout in the host preview window.

### ANGLE compositor output

On macOS, Cage can render into Podish-managed textures using the guest GBM and EGL libraries. The render node exports opaque PRIME fds for single-plane XRGB8888 and ARGB8888 BOs. Podish's EGLImage and guest renderbuffer references attach the texture to the compositor FBO. KMS waits for the producer fence and blits that same GPU resource to the host window.

```sh
dotnet "$cli" run --rm --kmsdrm --gles --rootfs "$rootfs" --user 1000:1000 \
    -e XDG_RUNTIME_DIR=/run/user/1000 -e LIBSEAT_BACKEND=builtin -e SEATD_VTBOUND=0 \
    -e WLR_BACKENDS=drm -e WLR_DRM_DEVICES=/dev/dri/card0 -e WLR_RENDERER=gles2 \
    -- /usr/bin/cage -d -- /usr/bin/weston-simple-shm
dotnet "$cli" run --rm --kmsdrm --gles --rootfs "$rootfs" -- /usr/local/bin/podish-kmsdrm-image-probe
```

This path supports one managed compositor context. In that context, guest `GL_TEXTURE_2D` objects can sample the same EGLImage with independent sampler parameters. Respecifying a texture detaches it from the shared image, with a GPU copy when the base level must be preserved. The image probe covers shared sampling, texture redefinition and the lifetime of FBOs after destroying their EGLImage and closing PRIME and GEM handles.

Wayland EGL clients can also submit single-plane XRGB8888 buffers to the guest compositor. Guest EGL obtains a virtual PRIME fd from the render node and sends it over `zwp_linux_dmabuf_v1`. ANGLE blits the client pbuffer into the managed BO and the compositor samples the same GPU storage. EGL fences order producer writes and compositor reads. `wl_buffer.release` controls reuse of the three GPU slots. Clients fall back to `wl_shm` when the server does not advertise the format or rejects the import.

For the packaged game tests, use the Debian rootfs from the earlier smoke-test instructions. The launch fixtures set the GLES profile before the first SDL window and check the engine's GPU swap log.

```sh
tests/gles/install-probe.sh "$rootfs"
tests/gles/run.sh "$rootfs" kmsdrm doom
tests/gles/run.sh "$rootfs" kmsdrm supertux
```

With the GLES/SSE2 build of SuperTuxKart installed at `/usr/local/bin/supertuxkart-gles-sse2` in the rootfs, run a 30-second Lighthouse race profile at 640×480.

```sh
tests/gles/run.sh "$rootfs" kmsdrm supertuxkart
```

To run the same race through the guest compositor, install Cage and its dependencies in the STK rootfs. The launcher provides a fresh temporary directory for each run.

```sh
tests/gles/run.sh "$rootfs" cage supertuxkart
```

Debian Bookworm's wlroots 0.15.1 loads the texture import entry point when the renderer advertises the external texture extension. Podish supports this path for managed RGB EGLImages and direct external sampler declarations in ESSL 1.00. Podish keeps external texture bindings and lifetimes separate and lowers these shader declarations to `sampler2D` for the host compiler. Require GPU submissions in the launcher with the following command.

```sh
PODISH_REQUIRE_WAYLAND_GPU=1 tests/gles/run.sh "$rootfs" cage supertuxkart
```

### External EGLImage test environment

Use an i386 rootfs with the original Cage and wlroots packages, the STK binary above, and the DRM/GBM headers needed by `tests/kmsdrm/install-probe.sh`. The installer also accepts `DRM_HEADERS` and `GBM_HEADERS` from the host. Build and use the guest libraries from the same checkout as the CLI.

```sh
export PODISH_GLES_GUEST_DIR="$PWD/artifacts/podish-gles/guest"
graphics/build-guest.sh "$PODISH_GLES_GUEST_DIR"
dotnet build Podish.Cli -c Release --nologo
tests/kmsdrm/install-probe.sh "$rootfs"
tests/kmsdrm/run-external-image.sh "$rootfs"
```

Pass a case name after the rootfs to run it independently.

| Case | Checks |
| --- | --- |
| `control` | The same PRIME import, image lifetime, shader reflection and pixel checks with ordinary 2D samplers. |
| `shader` | Compile mixed ESSL 1.00 source supplied in multiple strings with explicit lengths, even across an identifier. Read back the original source and reflect both sampler types after linking. |
| `state` | Separate 2D and external bindings by target and unit, default parameters, invalid mipmap and wrap operations, and deletion. |
| `sampling` | Sample an RGB EGLImage beside a regular 2D texture, reject mixed active sampler types on the same unit, reassign the sampler units, and check updated pixels after releasing the PRIME fd, EGLImage and producer objects. |
| `cage` | Run the 30-second STK profile under the original compositor and require the client to submit GPU buffers. |

Run all cases to validate the extension and the unmodified compositor together.

- The `Source2DTargetExternal` case verifies pixels after aliasing an EGLImage as an external texture. The Podish probe supplies the image through PRIME. [ANGLE ImageTest](https://chromium.googlesource.com/angle/angle/+/bafb59e142a3754e7c081bc5402b2cd81fdc62ab/src/tests/gl_tests/ImageTest.cpp#7100)
- The EGL image format tests exercise rendering with an external texture and arrays of external samplers. The first Podish cases exercise the single-image and mixed-sampler paths. [Khronos EGL Image Format Tests](https://github.com/KhronosGroup/VK-GL-CTS/blob/main/modules/egl/teglImageFormatTests.cpp#L824)
- The DMA-BUF YUV cases cover multi-plane images and color conversion. [Piglit DMA-BUF YUV sampling](https://cgit.freedesktop.org/piglit/tree/tests/spec/ext_image_dma_buf_import/sample_yuv.c)

### Buffer ownership and Vulkan compatibility

- Keep a DRM GEM handle scoped to the open file description. A KMS framebuffer retains its backing buffer after the handle is closed, and mappings retain their own backing reference. A future PRIME export must create an independently owned virtual file usable with `SCM_RIGHTS`. An imported file produces a handle in the importing DRM file's namespace. [DRM UAPI](https://docs.kernel.org/gpu/drm-uapi.html) · [dma-buf lifetime and synchronization](https://docs.kernel.org/driver-api/dma-buf.html)
- Keep EGL swap, KMS flip and buffer release separate. SDL swaps EGL, locks the GBM front BO and then registers and submits it to KMS. Its release of the prior BO follows the flip event. GPU-backed scanout must track both the producer fence and completion of the presenter's last access before letting GBM recycle the buffer. [SDL2 KMSDRM swap path](https://github.com/libsdl-org/SDL/blob/release-2.26.5/src/video/kmsdrm/SDL_kmsdrmopengles.c)
- Keep the KMS objects independent of the host rendering API. The current implementation uses standard EGL pbuffer sharing. Backends that need cross-API interop can implement GPU scanout buffers using IOSurface or host dma-buf with its plane geometry and format modifier. [ANGLE IOSurface extension](https://chromium.googlesource.com/angle/angle/+/2ecfc5a1c29357c6e16555298771cc4d68f20d90/extensions/EGL_ANGLE_iosurface_client_buffer.txt) · [Vulkan DRM format modifiers](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_image_drm_format_modifier.html)
- Vulkan WSI can reuse display objects, host buffer storage and presentation completion. Its swapchain will still own image acquisition, queue semaphore waits and present modes. Direct display can use the KMS path, and Wayland WSI submits through the compositor. A guest Vulkan ICD or a protocol such as Venus is a separate renderer transport. A macOS Vulkan backend that supports `VK_EXT_metal_objects` can import compatible IOSurface or Metal resources; probe that capability on the selected driver. [Vulkan WSI](https://docs.vulkan.org/spec/latest/chapters/VK_KHR_surface/wsi.html) · [Vulkan Metal interop](https://docs.vulkan.org/features/latest/features/proposals/VK_EXT_metal_objects.html) · [Mesa Venus requirements](https://docs.mesa3d.org/drivers/venus.html)
