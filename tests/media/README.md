# Windows and Linux media smoke tests

Run the same Debian i386 guest on either host. Build the fixture once from
Linux (WSL2 is fine) with Docker or `CONTAINER_ENGINE=podman`:

```sh
bash tests/media/build-fixture.sh media-fixture
dotnet build Podish.Cli -c Release -m:1
python tests/media/run.py --cli Podish.Cli/bin/Release/net10.0/Podish.Cli.dll --fixture media-fixture
```

On Windows, point the same command at the Windows CLI and fixture directory.
Use `--dotnet <path>` if .NET 10 is installed outside `PATH`. If a Windows
clang-cl build fails to link generated SuperOpcodes, build the smoke CLI with
`-p:EnableSuperOpcodes=OFF` as in CI.

The probes cover framebuffer ioctl and shared memory, Wayland `xdg_shell`
configure and a shared-memory frame with callback/release, KMS modeset and
page-flip events plus a pixel check on the CPU preview, and PulseAudio PCM
playback/drain. Each also requires evidence from Podish's host SDL backend.
Pass `--headless` to use SDL dummy video/audio. The Windows/Linux CI smoke
job runs these four headlessly; it does not exercise real windows or speakers.
The runner uses an isolated OCI store and `overlay-tmpfs` so the media probes
do not depend on the host SQLite installation.

## EGL and KMS direct presentation

Set `PODISH_ANGLE_DIR` to the host ANGLE directory (for example, from the
Electron 38.8.0 release for the host). KMS needs a real SDL window and an
available Vulkan device. On Linux, `SDL_VIDEODRIVER=x11` selects the tested X11
path; the Wayland test flag always refers to the **guest** protocol and also
works on a Win32 or X11 host.

```sh
python tests/media/run.py --cli Podish.Cli/bin/Release/net10.0/Podish.Cli.dll --fixture media-fixture \
    --case fbdev-egl --case fbdev-es3 --case wayland-egl --case kms-egl --case kms-es3
```

The KMS probe requires a host EGL swap after GPU synchronization. It fails if
only the guest page-flip event completes without presenting a frame.

The `fbdev-es3` and `kms-es3` cases require an ANGLE backend with GLES 3.0.
`fbdev-es3` also runs with `--headless`.

## SuperTuxKart

Build the upstream 1.4 release as i386 with `USE_GLES2=ON` and SSE2 math. The
builder downloads the official source and assets, verifies the archive
checksum and exports a rootfs tar. It needs several GB of disk space.

```sh
bash tests/media/build-stk.sh stk-fixture
python tests/media/run.py --cli Podish.Cli/bin/Release/net10.0/Podish.Cli.dll --fixture media-fixture \
    --stk-rootfs stk-fixture/stk-rootfs.tar --logs stk-logs
```

The runner uses guest SDL KMSDRM at 640×480 and checks that the 30-second
Lighthouse profile completes and at least eight direct host EGL swaps occur.
Use `--require-renderer 'Radeon'` (substitute your adapter name) to reject a
software Vulkan device; the host renderer is recorded from `GL_RENDERER`.
Swap counts alone do not check image contents. For a visual check, make sure
the loading text and race HUD are upright. At the loading-to-race transition
SDL KMSDRM may submit a newly recreated surface before drawing to it; that
frame should be black, not a corrupted copy of an earlier loading frame.

On WSL2, the default Vulkan loader may pick Mesa's CPU renderer. To exercise
the physical GPU, use a Mesa build with the Dozen Vulkan ICD, select its JSON
with `VK_DRIVER_FILES`, and use `SDL_VIDEODRIVER=x11` under WSLg. Check that
the test reports both `Dozen` and the physical adapter name. Native Linux can
use its usual Vulkan ICD; it does not need Dozen.
