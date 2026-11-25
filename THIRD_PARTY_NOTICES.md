# Third-Party Notices

This document lists all third-party libraries used in this project along with their authors and licenses.

## Window decorations

The bundled Catppuccin Latte window decoration uses the Catppuccin for Openbox theme and titlebar icons. [Upstream revision](https://github.com/catppuccin/openbox/tree/bb1c8662898b156e2b5425d4616bcc8ae4cfeeb2/themes/catppuccin-latte/openbox-3) · [MIT license](Podish.Wayland/Themes/catppuccin-latte/LICENSE)

The compositor rasterizes SVG application icons with Svg.Skia. [Svg.Skia 1.0.0.18 and MIT license](https://www.nuget.org/packages/Svg.Skia/1.0.0.18)

## Guest graphics headers

The EGL, GLES2, GLES3 and KHR headers under `graphics/guest/include` come from the Khronos EGL and OpenGL registries and retain their MIT and Apache 2.0 notices. [Apache 2.0 license](licenses/Apache-2.0.txt) [MIT license for gl2.h and gl3.h](graphics/guest/include/GLES2/LICENSE) [EGL Registry](https://github.com/KhronosGroup/EGL-Registry) · [OpenGL Registry](https://github.com/KhronosGroup/OpenGL-Registry) · [GLES3 header revision](https://github.com/KhronosGroup/OpenGL-Registry/tree/6af574a14089ccfee87efe230ebcdd8742859813/api/GLES3)

The bundled Wayland EGL backend header is from Wayland 1.22.0 and retains its MIT license notice. [Wayland EGL header](https://gitlab.freedesktop.org/wayland/wayland/-/blob/1.22.0/egl/wayland-egl-backend.h)

Podish loads a host-provided ANGLE build for the optional GLES bridge. [ANGLE license](https://chromium.googlesource.com/angle/angle/+/main/LICENSE)

## Apple guest Vulkan

`PodishVenus.framework` links the Venus backend of virglrenderer and the packaged KosmicKrisp driver from Mesa 26.2.7. The frameworks include applicable Mesa, virglrenderer, and Khronos Vulkan-Headers license notices. [virglrenderer (MIT)](https://github.com/emkey1/virglrenderer/tree/a6a179c72ace8e8e1cdbb267a007997be0318ca6) · [Mesa licenses](https://docs.mesa3d.org/license.html) · [Vulkan-Headers](https://github.com/KhronosGroup/Vulkan-Headers/tree/6802bb4733b63ed5efd3adb308a6c885ef180ea1)

The Vulkan compositor uses DiligentCore under Apache 2.0. The build embeds its shader binaries and includes the applicable license notices in the framework. [DiligentCore license](https://github.com/DiligentGraphics/DiligentCore/blob/c5cfdc4a4f08f2726c903b9c35782416c1eaccb7/License.txt)

The experimental Host Zink build bundles Mesa Zink and uses libepoxy for native OpenGL dispatch in virglrenderer. The corresponding frameworks include their license notices. [Mesa licenses](https://docs.mesa3d.org/license.html) · [libepoxy license](https://github.com/anholt/libepoxy/blob/c84bc9459357a40e46e2fec0408d04fbdde2c973/COPYING)

The Debian rootfs builds Mesa 25.0.7 for its i386 Venus and Zink drivers. The local Wayland EGL and Vulkan WSI changes retain the upstream files’ MIT license. [Mesa 25.0.7 source archive](https://archive.mesa3d.org/mesa-25.0.7.tar.xz)

## C++ Third-Party Libraries

### simde

| Property | Value |
|----------|-------|
| Repository | https://github.com/simd-everywhere/simde |
| Version | 0.8.2 |
| License | MIT |

### unordered_dense

| Property | Value |
|----------|-------|
| Repository | https://github.com/martinus/unordered_dense |
| Version | 4.8.1 |
| License | MIT |

### Berkeley SoftFloat 3e

| Property | Value |
|----------|-------|
| Location | libfibercpu/3rdparty/softfloat |
| Author | John R. Hauser |
| License | Custom (see SoftFloat README) |

---

## Rust Third-Party Libraries

### smoltcp

| Property | Value |
|----------|-------|
| Version | 0.12.0 |
| License | MIT OR Apache-2.0 |

### Transitive Dependencies

The following Rust crates are transitive dependencies of smoltcp:

| Crate | Version | License |
|-------|---------|---------|
| bitflags | 1.3.2 | MIT OR Apache-2.0 |
| byteorder | 1.5.0 | MIT OR Apache-2.0 |
| cfg-if | 1.0.4 | MIT OR Apache-2.0 |
| defmt | 1.0.1 | MIT OR Apache-2.0 |
| defmt-macros | 1.0.1 | MIT OR Apache-2.0 |
| defmt-parser | 1.0.0 | MIT OR Apache-2.0 |
| hash32 | 0.3.1 | MIT OR Apache-2.0 |
| heapless | 0.8.0 | MIT OR Apache-2.0 |
| managed | 0.8.0 | MIT OR Apache-2.0 |
| proc-macro-error2 | 2.0.1 | MIT OR Apache-2.0 |
| proc-macro-error-attr2 | 2.0.0 | MIT OR Apache-2.0 |
| proc-macro2 | 1.0.106 | MIT OR Apache-2.0 |
| quote | 1.0.45 | MIT OR Apache-2.0 |
| stable_deref_trait | 1.2.1 | MIT OR Apache-2.0 |
| syn | 2.0.117 | MIT OR Apache-2.0 |
| thiserror | 2.0.18 | MIT OR Apache-2.0 |
| thiserror-impl | 2.0.18 | MIT OR Apache-2.0 |
| unicode-ident | 1.0.24 | MIT OR Apache-2.0 |

---

## C# Third-Party Libraries (NuGet Packages)

### Iced (CPU tests and asset validation only)

| Property | Value |
|----------|-------|
| Repository | https://github.com/icedland/iced |
| Version | 1.21.0 |
| License | MIT |

### FEX instruction tests (test assets only)

| Property | Value |
|----------|-------|
| Repository | https://github.com/FEX-Emu/FEX |
| Revision | 8e95a994a014b0e68fa38b7bf17d84987102e374 |
| License | MIT; upstream notices retained with assets in tests/cpu |

### Microsoft.Extensions.Logging

| Property | Value |
|----------|-------|
| Version | 8.0.0 |
| License | MIT |

### Microsoft.Extensions.Logging.Abstractions

| Property | Value |
|----------|-------|
| Version | 8.0.0 |
| License | MIT |

### Microsoft.Extensions.Logging.Console

| Property | Value |
|----------|-------|
| Version | 8.0.0 |
| License | MIT |

### LibObjectFile

| Property | Value |
|----------|-------|
| Version | 2.1.0 |
| License | MIT |

### System.CommandLine

| Property | Value |
|----------|-------|
| Version | 2.0.0-beta4.22272.1 |
| License | MIT |

### SkiaSharp

| Property | Value |
|----------|-------|
| Version | 2.88.9 |
| License | MIT |

### SkiaSharp.NativeAssets.macOS

| Property | Value |
|----------|-------|
| Version | 2.88.9 |
| License | MIT |

### SkiaSharp.NativeAssets.Linux.NoDependencies

| Property | Value |
|----------|-------|
| Version | 2.88.9 |
| License | MIT |

### Silk.NET.SDL

| Property | Value |
|----------|-------|
| Version | 2.23.0 |
| License | MIT |

### SQLitePCLRaw.core

| Property | Value |
|----------|-------|
| Version | 2.1.6 |
| License | Custom (see SQLitePCLRaw LICENSE) |

### SQLitePCLRaw.provider.sqlite3

| Property | Value |
|----------|-------|
| Version | 2.1.6 |
| License | Custom (see SQLitePCLRaw LICENSE) |

### MessagePack

| Property | Value |
|----------|-------|
| Version | 3.1.4 |
| License | MIT |

### Microsoft.NET.Test.Sdk

| Property | Value |
|----------|-------|
| Version | 17.5.0, 17.8.0 |
| License | MIT |

### Moq

| Property | Value |
|----------|-------|
| Version | 4.20.72 |
| License | BSD-3-Clause |

### xunit

| Property | Value |
|----------|-------|
| Version | 2.4.2, 2.5.3, 2.6.6 |
| License | MIT |

### xunit.runner.visualstudio

| Property | Value |
|----------|-------|
| Version | 2.4.5, 2.5.3, 2.5.6 |
| License | MIT |

### coverlet.collector

| Property | Value |
|----------|-------|
| Version | 3.2.0, 6.0.0 |
| License | MIT |

---

## JavaScript/TypeScript Third-Party Libraries (npm packages)

### @tailwindcss/vite

| Property | Value |
|----------|-------|
| Version | 4.2.2 |
| License | MIT |

### @xterm/addon-fit

| Property | Value |
|----------|-------|
| Version | 0.11.0 |
| License | MIT |

### @xterm/addon-webgl

| Property | Value |
|----------|-------|
| Version | 0.19.0 |
| License | MIT |

### @xterm/xterm

| Property | Value |
|----------|-------|
| Version | 6.0.0 |
| License | MIT |

### react

| Property | Value |
|----------|-------|
| Version | 19.2.4 |
| License | MIT |

### react-dom

| Property | Value |
|----------|-------|
| Version | 19.2.4 |
| License | MIT |

### tailwindcss

| Property | Value |
|----------|-------|
| Version | 4.2.2 |
| License | MIT |

### @eslint/js

| Property | Value |
|----------|-------|
| Version | 9.39.4 |
| License | MIT |

### @types/react

| Property | Value |
|----------|-------|
| Version | 19.2.14 |
| License | MIT |

### @types/react-dom

| Property | Value |
|----------|-------|
| Version | 19.2.3 |
| License | MIT |

### @vitejs/plugin-react

| Property | Value |
|----------|-------|
| Version | 6.0.1 |
| License | MIT |

### eslint

| Property | Value |
|----------|-------|
| Version | 9.39.4 |
| License | MIT |

### eslint-plugin-react-hooks

| Property | Value |
|----------|-------|
| Version | 7.0.1 |
| License | MIT |

### eslint-plugin-react-refresh

| Property | Value |
|----------|-------|
| Version | 0.5.2 |
| License | MIT |

### globals

| Property | Value |
|----------|-------|
| Version | 17.4.0 |
| License | MIT |

### vite

| Property | Value |
|----------|-------|
| Version | 8.0.1 |
| License | MIT |

---

## Build Tools

### CPM.cmake

| Property | Value |
|----------|-------|
| Repository | https://github.com/cpm-cmake/CPM.cmake |
| Version | 0.40.2 |
| License | MIT |
