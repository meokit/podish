# IA-32 framebuffer games and X11

Podish CLI can display a guest Linux framebuffer in a host SDL2 window. Run the commands below from the repository root. The builders download checksum-pinned packages into a new root filesystem directory. They need Bash, curl, ar, tar and shasum. The host needs an SDL2 runtime and the normal Podish CLI build tools. Set `PODISH_CLI` to the path of an already built Podish.Cli.dll to skip the default Release build.

## Games on SDL 1.2 fbcon

```sh
tests/fbdev-games/build-rootfs.sh /tmp/podish-classics
tests/fbdev-games/run.sh /tmp/podish-classics doom -warp 1 1
tests/fbdev-games/run.sh /tmp/podish-classics supertux /usr/share/games/supertux/levels/world1/level1.stl
```

The games use the Debian i386 builds of PrBoom 2.5.0 and SuperTux 0.1.3 with SDL 1.2.14. PrBoom loads the DOOM 1.9 shareware IWAD. Omit the level argument to start SuperTux at its menu. Both launchers disable game audio. Close the SDL window to stop the guest.

## SDL 1.0.8 fbcon

The independent probe uses the upstream i386 SDL 1.0.8 library. Installing it also requires bsdtar and Zig with an x86 Linux target.

```sh
tests/fbdev-games/install-sdl10-probe.sh /tmp/podish-classics
dotnet run --project Podish.Cli -- run --rm --fbdev --rootfs /tmp/podish-classics \
    -e SDL_VIDEODRIVER=fbcon -- /bin/sh -c 'exec /usr/local/bin/sdl10-probe --input </dev/tty1'
```

Press A and click the left mouse button in the window. The probe verifies its library version, video driver, 320×240 RGB565 pixel readback and input events. Omit `--input` for a noninteractive pixel test. SDL 1.0.8 uses standard input as its keyboard console.

## X.Org and Openbox

```sh
tests/fbdev-games/build-rootfs.sh /tmp/podish-x11 x11
tests/fbdev-games/run-x11.sh /tmp/podish-x11
```

This starts X.Org 1.7.7 with the fbdev, kbd and mouse drivers, then Openbox, xeyes and xclock at 800×600. Each session uses a private tmpfs for its X11 socket and display lock. Right-click the desktop to open a terminal, clock or eyes window. The desktop uses software rendering.

For a headless smoke test, use the host's SDL dummy video driver. The guest still runs the X.Org fbdev server. The test checks display geometry, the windows managed by Openbox and the pseudoterminal allocated by xterm before exiting.

```sh
SDL_VIDEODRIVER=dummy tests/fbdev-games/run-x11.sh /tmp/podish-x11 smoke
```

## Original Linux DOOM 1.10 on X11

With both root filesystems built, compile the original id Software source for IA-32 and copy the shareware IWAD into the X11 root filesystem. This optional build needs Zig, make and X11 development headers. On macOS the installer looks in the XQuartz include directory. Set `X11_HEADERS` for a different location.

```sh
tests/fbdev-games/install-linux-doom.sh /tmp/podish-x11 /tmp/podish-classics
tests/fbdev-games/run-x11.sh /tmp/podish-x11 doom
```

This launches E1M1 in an Openbox window on an 8-bit X.Org screen so the game can install its palette. The original external sound server is optional and is absent from this image.

## Using another root filesystem

Pass `run --fbdev --fbdev-size WIDTHxHEIGHT` to the CLI. The dimensions select the initial and maximum visible size and default to 640×480. Programs can change the visible mode, use a palette or pan within video memory. The display accepts 8-bit indexed and 15-, 16-, 24- and 32-bit color. The guest keyboard and PS/2 pointer are available at `/dev/tty1` and `/dev/input/mice`. For SDL 1.x applications set the **guest** environment with `-e SDL_VIDEODRIVER=fbcon`.
