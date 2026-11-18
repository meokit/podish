# ANGLE-backed EGL and OpenGL ES

Podish CLI can expose EGL and OpenGL ES 2.0 to IA-32 Linux containers. Enable it with `run --gles` and choose `--fbdev` or `--wayland-server` for visible output. A container with neither display option can use offscreen pbuffers.

## Build and launch

From the repository root, build the IA-32 libraries with Zig and point Podish at an ANGLE build for the **host** OS and CPU.

```sh
graphics/build-guest.sh
export PODISH_ANGLE_DIR=/path/to/host/angle/libraries
dotnet build Podish.Cli -c Release
```

On macOS, an Electron distribution's `Libraries` directory is one way to obtain the matching `libEGL.dylib` and `libGLESv2.dylib`. Podish selects ANGLE's Metal backend there. Other desktop platforms use ANGLE's default backend. The guest build targets glibc 2.11 or later. Set `PODISH_GLES_GUEST_DIR` if you build the guest libraries outside `artifacts/podish-gles/guest` or launch Podish from another working directory. [ANGLE build instructions](https://chromium.googlesource.com/angle/angle/+/main/doc/DevSetup.md)

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
