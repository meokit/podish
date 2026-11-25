# Apple desktop graphics

Choose the host compositor and guest graphics stack with `PODISH_GRAPHICS_BACKEND`. Set it consistently for the app, NativeAOT, and Debian rootfs builds.

Vulkan builds require Metal 4 on Apple silicon macOS 26 or on a physical iOS 26 device. Build the GLES variant for the iOS Simulator.

| Build | Host compositor | Guest graphics |
| --- | --- | --- |
| `vulkan` | DiligentCore Vulkan on the same KosmicKrisp implementation used by Venus | Mesa Venus on `/dev/dri/renderD129`, with Zink desktop OpenGL and EGL under Wayland |
| `gles` | ANGLE Metal | ANGLE EGL and GLES under `/run/podish-gles`, with the original virtual DRM devices |

The Swift view supplies the `CAMetalLayer` and forwards Apple input to the built-in Wayland compositor in both builds. The compositor owns the scene, frame callbacks, and buffer release. Each package embeds its selected graphics frameworks and a matching rootfs.

## Vulkan presentation

The Debian Mesa WSI blits to a linear Venus PRIME buffer and commits it with an acquire `sync_file` using `zwp_linux_explicit_synchronization_v1`. Podish resolves the virtual fence and waits asynchronously before enqueueing the frame. Wayland hands the compositor the PRIME backing, plane offset, row pitch, dimensions, and DRM format.

Venus and the Vulkan compositor share the same backing on the same physical Metal device. The compositor samples the linear allocation through a Vulkan storage buffer. The Wayland lease keeps the allocation alive until the surface stops using it and the last GPU submission that sampled it completes. CPU-rendered Wayland SHM surfaces, decorations, and icons use separate texture uploads.

In the Vulkan build, the packaged Qt 6 launcher uses a desktop OpenGL 2.1 context through Zink. SuperTuxKart defaults to its Vulkan renderer. The Mesa EGL vendor and Vulkan ICD selectors are configured by the guest desktop session. The private Mesa build targets Wayland EGL. X11 applications that create GLX contexts require separate XWayland and GLX support.

The virtio-gpu interface follows [iSH-AOK’s Apple Venus implementation](https://github.com/emkey1/ish-AOK/tree/9c02c6538103a4b185fce489fab069ff2b1b35de). The packaged Mesa KosmicKrisp driver supplies Vulkan entry points directly to Venus and DiligentCore. Venus imports the backing of linear PRIME images through `VK_EXT_external_memory_host`. DiligentCore imports the same backing as a storage buffer and uses the Vulkan image’s actual plane offset and row pitch. The guest sees virtual DMA-BUF descriptors and `sync_file` fences, with fence completion driven by the host renderer.

The i386 Venus driver issues explicit memory barriers when consuming the command ring head, publishing the tail, and checking idle status after submission. These barriers order shared-memory accesses between the emulated x86 producer and the native ARM consumer without waiting for GPU work.

### Guest OpenGL 4.3 core compatibility mode

Guest Zink over Venus allows applications to open an OpenGL 4.3 core context. Mesa uses the device's reported Vulkan features to set OpenGL limits and extensions. Mesa returns a GL error when an application invokes an unavailable operation, including geometry shaders, transform feedback, non-solid polygon fill, nonstandard sampler border colors, and unsupported RGB32 buffer texture formats. Compatibility-profile applications retain the driver's negotiated version.

The guest desktop session enables this mode by default. Set `PODISH_GUEST_GL43=0` for an application to use the driver's ordinary version negotiation. Run `podish-zink-probe --gl43` in the bundled guest to verify the runtime errors, GLSL 4.30 compute with an SSBO, and Wayland presentation.

## Build

Use one of these commands from the repository root. See the [Apple app build instructions](../PodishApp/README.md) for tooling and individual build steps.

```sh
PODISH_GRAPHICS_BACKEND=vulkan bash scripts/apple/package-apple.sh --full
PODISH_GRAPHICS_BACKEND=gles bash scripts/apple/package-apple.sh --full
```

## Maintain the KosmicKrisp patches

[The driver build script](../scripts/apple/prepare-kosmickrisp.sh) pins the upstream Mesa release and its SHA-256. It applies the KosmicKrisp patches in `scripts/apple/venus/` in the order listed in `mesa_patches`. Keep one patch per logical change in Git and edit Mesa in a separate checkout when preparing patches. The Mesa archive, patched source trees, and built frameworks are generated under `PodishApp/artifacts/` by default. Changing the Mesa checksum, patch contents, or patch order selects a fresh source tree and build directory.

When upgrading Mesa, update the release and checksum together. Remove patches whose fixes are in the new release and refresh the remaining patches against a clean tree. Run the patch check before building the macOS and iOS frameworks. The check unpacks and patches a fresh copy using the pinned archive, with no Xcode or compiler needed.

```sh
bash scripts/apple/prepare-kosmickrisp.sh --check-patches
bash scripts/apple/prepare-kosmickrisp.sh macosx iphoneos
```

## Verify

Start each app and check that the launcher reports accelerated graphics. The Vulkan build should report Zink on Venus. The GLES build should report ANGLE. Check search, scrolling, window resize, and switching between the launcher and game.

In the guest, run the Lighthouse race to exercise repeated presentation.

```sh
supertuxkart --no-sound --race-now --track=lighthouse --kart=tux \
  --numkarts=2 --ai=puffy --screensize=640x480 --seed=23 --profile-time=10
```

`PODISH_AUTOSTART_APP` runs a guest command at desktop startup. `PODISH_STATE_DIR` isolates the test app's Guest state. For a Simulator launch via `simctl`, prefix these variables with `SIMCTL_CHILD_`. Run the game with `WAYLAND_DEBUG=1` to observe the DMA-BUF attachments, acquire fences, and buffer release in the Vulkan build.

## Experimental Host Zink

`PODISH_HOST_ZINK=1` builds an additional native Mesa framework and enables the VirGL bridge inside PodishVenus. The build goes to `dist/apple/vulkan-host-zink`. Set `PODISH_GUEST_GL_DRIVER=virgl` on the app process to select the experimental Guest GL path. Guest Vulkan uses Venus. The default Guest GL path is Zink on Venus.

```sh
PODISH_HOST_ZINK=1 bash scripts/apple/package-apple.sh --full
python3 tests/vulkan/benchmark-stk.py command host-zink
python3 tests/vulkan/benchmark-stk.py report host-zink /path/to/guest.log
```

Run both Lighthouse variants with the same OpenGL game settings. The benchmark uses one kart at 640×480 and seed 23, and keeps the Guest GL libraries resident through SuperTuxKart shutdown. At startup, the Apple App raises its soft `RLIMIT_NOFILE` up to `OPEN_MAX`, within the process hard limit. All Guest apps share this host FD budget. The report requires the expected hardware renderer, a profile summary, and exit status zero before publishing FPS. Use `guest-zink` as the command and report parameter for the Guest Zink baseline.

The base Mesa KosmicKrisp driver exposes conditional rendering. Transform feedback remains absent and limits the tested Mesa 25.0.7 Host Zink to desktop OpenGL 2.1. Treat the Host Zink path as experimental pending GL capability and shared scanout verification. The default Guest Zink path runs over Venus.
