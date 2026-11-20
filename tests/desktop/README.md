# LXQt Wayland desktop smoke test

This test runs LXQt as a regular guest user on Labwc with Pixman and the virtual KMS framebuffer. Labwc uses libseat's embedded seatd backend and libinput to open the virtual keyboard and relative mouse. The test requires both devices, a running LXQt session, the panel and desktop layer surfaces, and mapped PCManFM-Qt and QTerminal windows. It captures the real KMS scanout via `/dev/fb0`, checks that the Debian LXQt wallpaper, panel, and terminal text are visible in their expected regions, and verifies that embedded seatd and the guest exit.

Use an IA-32 Debian Trixie root filesystem tar with `labwc`, `xwayland`, `lxqt-session`, `lxqt-panel`, `pcmanfm-qt`, `lxqt-runner`, `lxqt-notificationd`, `qterminal`, `qt6-wayland`, `layer-shell-qt`, `dbus-daemon`, and `dbus-bin`. Include the regular core utilities, `sed`, the DejaVu fonts, and a non-root account named `user` with a writable home. Pass `--user NAME` if the account has a different name. Give `/tmp` and `/tmp/.X11-unix` mode `1777` and include a valid machine ID. The test boots the archive with a fresh writable overlay each time.

On an amd64 Linux host with IA-32 execution available, one way to prepare the image is:

```sh
packages=labwc,xwayland,lxqt-session,lxqt-panel,pcmanfm-qt,lxqt-runner,lxqt-notificationd,qterminal,qt6-wayland,qt6-svg-plugins,layer-shell-qt,dbus-daemon,dbus-bin,fonts-dejavu-core,fonts-dejavu-mono,kf6-breeze-icon-theme,xdg-user-dirs,passwd
mmdebstrap --mode=unshare --architectures=i386 --variant=minbase --include="$packages" \
    --customize-hook='chroot "$1" useradd -m -U -u 1000 user' \
    --customize-hook='install -d -m 1777 "$1/tmp/.X11-unix"' \
    --customize-hook='chroot "$1" dbus-uuidgen --ensure=/etc/machine-id' \
    trixie lxqt-i386.tar https://deb.debian.org/debian
```

Run from the worktree with .NET and Zig installed:

```sh
graphics/build-guest.sh
dotnet build Podish.Cli -c Release
python3 tests/desktop/run.py --rootfs lxqt-i386.tar
```

The default run uses SDL's dummy backend and leaves `desktop.png`, `engine.log`, and `guest.log` under `artifacts/lxqt-desktop`. Add `--verify-input` to open an SDL window. Click QTerminal, type `seat-ok`, and press Enter. The guest terminal must receive that input before the screenshot is taken.

The virtual KMS and fbdev combination exposes one static seat with keyboard, relative pointer, three buttons, and wheel. The test selects the embedded seatd backend with VT binding disabled. Its LXQt session uses the distribution defaults from `/usr/share` and skips the PolicyKit agent because the fixture runs without a system bus and login service.
