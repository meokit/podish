#!/bin/sh
printf '\n  Podish / LXQt Wayland\n\n'
printf '  Compositor: Labwc %s\n' "$LABWC_VER"
printf '  Seat: seat0 (libseat)\n'
printf '  Display: %s (DRM/KMS)\n\n' "$WAYLAND_DISPLAY"
if [ "${PODISH_DESKTOP_INPUT_CHECK:-0}" = 1 ]; then
    printf '  Type seat-ok and press Enter.\n  > '
    while IFS= read -r line; do
        if [ "$line" = seat-ok ]; then
            printf '\n  Keyboard input confirmed.\n'
            : > "$XDG_RUNTIME_DIR/input-ok"
            break
        fi
        printf '  > '
    done
fi
exec sleep infinity
