#!/bin/sh
set -eu
export PATH=/usr/local/bin:/usr/bin:/bin HOME=/root DISPLAY=:0
mode=${1:-desktop}
case "$mode" in desktop|smoke|doom) ;; *) exit 2 ;; esac

# Each guest needs its own X11 sockets and display lock, including after window close.
/bin/busybox mount -t tmpfs -o mode=1777 tmpfs /tmp

# The rootfs is extracted without Debian post-install scripts.
fontdir=/usr/share/fonts/X11/misc
if [ ! -s "$fontdir/fonts.dir" ]; then
    mkfontdir "$fontdir"
    cp /etc/X11/fonts/misc/xfonts-base.alias "$fontdir/fonts.alias"
fi

set -- vt1 -nolisten tcp -noreset -extension GLX
if [ "$mode" = doom ]; then
    [ -x /usr/local/bin/linuxxdoom ] || { echo 'Run install-linux-doom.sh first' >&2; exit 1; }
    set -- "$@" -depth 8
fi
(exec Xorg "$DISPLAY" "$@") &
server=$!
clients=
cleanup() {
    if [ -n "$clients" ]; then
        kill $clients 2>/dev/null || true
        for pid in $clients; do wait "$pid" 2>/dev/null || true; done
    fi
    kill "$server" 2>/dev/null || true
    wait "$server" 2>/dev/null || true
}
trap cleanup EXIT
attempt=0
until xdpyinfo >/dev/null 2>&1; do
    kill -0 "$server" || exit 1
    attempt=$((attempt + 1))
    [ "$attempt" -lt 60 ] || { echo 'X.Org startup timed out' >&2; exit 1; }
    sleep 1
done

(exec openbox --sm-disable) &
wm=$!
clients=$wm
if [ "$mode" = doom ]; then
    attempt=0
    until xprop -root _NET_SUPPORTING_WM_CHECK | /bin/busybox grep -Eq '0x[0-9a-f]+'; do
        kill -0 "$wm" || exit 1
        attempt=$((attempt + 1))
        [ "$attempt" -lt 60 ] || { echo 'Openbox startup timed out' >&2; exit 1; }
        sleep 1
    done
    DOOMWADDIR=/usr/share/games/doom /usr/local/bin/linuxxdoom -1 -warp 1 1
    exit
fi
(exec xeyes -geometry 140x100+25+40) &
clients="$clients $!"
(exec xclock -geometry 120x120+220+40) &
clients="$clients $!"
if [ "$mode" = desktop ]; then
    wait "$wm"
    exit
fi

attempt=0
until xprop -root _NET_CLIENT_LIST | /bin/busybox grep -Eq '0x[0-9a-f]+, 0x[0-9a-f]+'; do
    kill -0 "$wm" || exit 1
    attempt=$((attempt + 1))
    [ "$attempt" -lt 60 ] || { echo 'Openbox did not manage both clients' >&2; exit 1; }
    sleep 1
done
xdpyinfo | /bin/busybox grep -E 'version number|dimensions:'
xprop -root _NET_SUPPORTING_WM_CHECK _NET_CLIENT_LIST
windows=$(xwininfo -root -tree)
echo "$windows" | /bin/busybox grep '"xeyes"'
echo "$windows" | /bin/busybox grep '"xclock"'
xterm -e /bin/sh -c '/bin/busybox tty > /tmp/xterm-tty'
/bin/busybox grep '^/dev/pts/' /tmp/xterm-tty
