# Podish Desktop (iOS & macOS)

Podish embeds a complete i386 Debian Trixie graphical desktop directly inside a native iOS and macOS app.

## Architecture

- **Embedded Linux Desktop**: i386 Debian Trixie + Qt 6 Wayland + common LXQt apps (`qterminal`, `pcmanfm-qt`, `featherpad`, `lximage-qt`, `lxqt-archiver`, `qps`, `lxqt-config`, `screengrab`).
- **Touch-First Qt6 Launcher**: Custom C++ Qt6 Wayland launcher (`guest/podish-launcher/`) that opens immediately when the app starts, featuring kinetic finger-swipe scrolling, live search, category filters, `.desktop` app discovery, and a quick-launch dock.
- **Built-in Wayland Compositor + ANGLE Metal**: `Podish.Wayland` runs in-process over a virtual Unix socket (`/run/wayland-0`) and renders directly to a native `CAMetalLayer` through ANGLE (Metal backend) on both iOS and macOS, plus guest EGL/GLES passthrough.
- **Storage (`tarfs` + `silkfs`)**: The read-only Debian rootfs is mounted directly from an indexed `.tar` inside the app bundle (`PodishRootfs/blobs/rootfs.tar`) using `layerfs`, overlaid with a persistent writable `silkfs` SQLite-backed upper layer in `Application Support/Podish`.

## Build & Package Everything

From the repository root:

```bash
# 1. Fetch/wrap ANGLE Metal XCFrameworks (macOS arm64, iOS arm64, iOS Simulator arm64)
bash scripts/apple/prepare-angle.sh

# 2. Build IA-32 guest EGL/GLES/GBM libraries
bash graphics/build-guest.sh PodishApp/Resources/PodishGlesGuest

# 3. Build Debian i386 LXQt + Qt6 Launcher rootfs and tarfs index (Docker / OrbStack / Podman)
bash scripts/apple/build-debian-rootfs.sh

# 4. Build NativeAOT PodishCore.xcframework (osx-arm64, ios-arm64, iossimulator-arm64)
bash Podish.Core.Native/scripts/publish-static.sh

# 5. Build and package complete macOS (.app / .zip) and iOS (.ipa + simulator .app) bundles
bash scripts/apple/package-apple.sh
```

Or run the one-shot packaging script with `--full` to execute all steps end-to-end:

```bash
bash scripts/apple/package-apple.sh --full
```

Packaged outputs are placed in `dist/apple/`:
- `dist/apple/macos/Podish.app` and `dist/apple/Podish-macOS-arm64.zip`
- `dist/apple/Podish-iOS-arm64.ipa`
- `dist/apple/ios-simulator/Podish.app`
