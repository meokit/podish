#!/bin/sh
set -eu
export XDG_RUNTIME_DIR="$(mktemp -d)"
# LXQt's Wayland launcher includes /usr/share for its default theme and desktop profile.
export XDG_CONFIG_HOME="${XDG_CONFIG_HOME:-$HOME/.config}"
export XDG_CONFIG_DIRS=/etc:/etc/xdg:/usr/share
export XDG_MENU_PREFIX=lxqt-
export XDG_SESSION_TYPE=wayland XDG_CURRENT_DESKTOP=LXQt:labwc:wlroots XDG_SESSION_DESKTOP=lxqt
mkdir -p "$HOME/Desktop" "$XDG_CONFIG_HOME/autostart"
# The isolated smoke session has no system PolicyKit service or login session.
printf '[Desktop Entry]\nType=Application\nName=LXQt PolicyKit Agent\nHidden=true\n' \
    > "$XDG_CONFIG_HOME/autostart/lxqt-policykit-agent.desktop"
export QT_QPA_PLATFORM=wayland QT_QPA_PLATFORMTHEME=lxqt
export LIBSEAT_BACKEND=builtin SEATD_VTBOUND=0
export WLR_BACKENDS=drm,libinput WLR_DRM_DEVICES=/dev/dri/card0
export WLR_RENDERER=pixman WLR_DRM_NO_ATOMIC=1 WLR_NO_HARDWARE_CURSORS=1
exec dbus-run-session -- labwc -d -c /run/desktop-test/labwc.xml -S /run/desktop-test/session.sh
