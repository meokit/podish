# Podish Desktop (iOS & macOS)

Podish embeds a complete i386 Debian Trixie graphical desktop directly inside a native iOS and macOS app.

## Architecture

- **Embedded Linux Desktop**: i386 Debian Trixie + Qt 6 Wayland + common LXQt apps (`qterminal`, `pcmanfm-qt`, `featherpad`, `lximage-qt`, `lxqt-archiver`, `qps`, `lxqt-config`, `screengrab`).
- **Touch-First Qt6 Launcher**: Custom C++ Qt6 Wayland launcher (`guest/podish-launcher/`) that opens immediately when the app starts, featuring kinetic finger-swipe scrolling, live search, category filters, `.desktop` app discovery, and a quick-launch dock.
- **Built-in Wayland compositor.** `Podish.Wayland` runs in process over `/run/wayland-0`. The Apple view provides a `CAMetalLayer` and sends AppKit or UIKit input to the Wayland server.
- **Apple graphics builds.** The Vulkan build uses DiligentCore and KosmicKrisp, supplies a Venus render node, and runs Zink desktop OpenGL in the guest. The GLES build uses ANGLE Metal and the original guest EGL and GLES devices.
- **Storage (`tarfs` + `silkfs`)**: The read-only Debian rootfs is mounted directly from an indexed `.tar` inside the app bundle (`PodishRootfs/blobs/rootfs.tar`) using `layerfs`, overlaid with a persistent writable `silkfs` SQLite-backed upper layer in `Application Support/Podish`.

## Build and package

Install Xcode, the .NET 10 SDK, Rust with the Apple targets, Python 3, and a container engine for the i386 Linux build. The Vulkan build also uses CMake, Meson, Ninja, `glslc`, and LLVM/Clang 21.1 for the native Mesa shader tools. Meson needs Python with Mako, Packaging, and PyYAML. Run from the repository root.

```sh
PODISH_GRAPHICS_BACKEND=vulkan bash scripts/apple/package-apple.sh --full
PODISH_GRAPHICS_BACKEND=gles bash scripts/apple/package-apple.sh --full
```

`vulkan` is the default. Each command builds its matching frameworks, Debian rootfs, NativeAOT library, and Apple packages. Run the same command without `--full` to repackage prepared artifacts. The native library and rootfs directories are specific to the selected graphics backend.

The Vulkan build uses Metal 4 on Apple silicon with macOS 26 or on a physical device with iOS 26. It builds pinned Mesa KosmicKrisp, DiligentCore, and virglrenderer from source. Run `bash scripts/apple/prepare-venus.sh macosx` or pass `iphoneos` to build the corresponding frameworks. Vulkan packages contain the macOS and iOS device apps. Select the GLES build to package the iOS Simulator app.

The package script writes the following files under `dist/apple/vulkan` or `dist/apple/gles`.

- `macos/Podish.app` and `Podish-macOS-arm64.zip`
- `Podish-iOS-arm64.ipa`
- `ios-simulator/Podish.app` for GLES builds
