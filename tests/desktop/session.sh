#!/bin/sh
set -eu

lxqt-session &
session=$!
terminal=
cleanup() {
    if [ -n "$terminal" ]; then
        kill "$terminal" 2>/dev/null || :
        wait "$terminal" 2>/dev/null || :
    fi
    kill "$session" 2>/dev/null || :
    wait "$session" 2>/dev/null || :
}
trap cleanup EXIT

for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if dbus-send --session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus \
        org.freedesktop.DBus.NameHasOwner string:org.lxqt.session |
        sed -n '/boolean true/p' | read -r _; then
        break
    fi
    if [ "$attempt" = 10 ]; then
        echo 'LXQt did not register on D-Bus' >&2
        exit 1
    fi
    kill -0 "$session"
    sleep 1
done
pcmanfm-qt --profile=lxqt --new-window /usr/share/lxqt &
qterminal -e /run/desktop-test/terminal.sh &
terminal=$!
for attempt in $(seq 1 30); do
    if [ -f /run/desktop-output/capture-ready ]; then
        break
    fi
    kill -0 "$session" "$terminal"
    sleep 1
done
test -f /run/desktop-output/capture-ready
kill -0 "$session" "$terminal"
if [ "${PODISH_DESKTOP_INPUT_CHECK:-0}" = 1 ]; then
    echo 'Waiting for keyboard input in QTerminal'
    for attempt in $(seq 1 60); do
        if [ -f "$XDG_RUNTIME_DIR/input-ok" ]; then
            break
        fi
        kill -0 "$terminal"
        sleep 1
    done
    test -f "$XDG_RUNTIME_DIR/input-ok"
    echo 'Wayland keyboard input reached QTerminal'
fi
sleep 1
/run/desktop-test/frame > /run/desktop-output/desktop.ppm
echo 'LXQt desktop frame captured'
