These protocol XML files are vendored from upstream Wayland projects and are intended to stay as canonical protocol descriptions.

Sources:
- `wayland.xml`
  - Upstream: `https://cgit.freedesktop.org/wayland/wayland/plain/protocol/wayland.xml`
  - Retrieved: `2026-03-24`
- `xdg-shell.xml`
  - Upstream: `https://cgit.freedesktop.org/wayland/wayland-protocols/plain/stable/xdg-shell/xdg-shell.xml`
  - Retrieved: `2026-03-24`
- `wlr-layer-shell-unstable-v1.xml`
  - Upstream: `https://github.com/swaywm/wlr-protocols/blob/master/unstable/wlr-layer-shell-unstable-v1.xml`
- `wlr-foreign-toplevel-management-unstable-v1.xml`
  - Upstream: `https://github.com/swaywm/wlr-protocols/blob/master/unstable/wlr-foreign-toplevel-management-unstable-v1.xml`
- `linux-dmabuf-v1.xml`
  - Upstream: `https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/stable/linux-dmabuf/linux-dmabuf-v1.xml`
- `linux-explicit-synchronization-unstable-v1.xml`
  - Upstream: `https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/unstable/linux-explicit-synchronization/linux-explicit-synchronization-unstable-v1.xml`
- `fractional-scale-v1.xml`
  - Upstream: `https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/staging/fractional-scale/fractional-scale-v1.xml`
- `viewporter.xml`
  - Upstream: `https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/stable/viewporter/viewporter.xml`
- `xdg-output-unstable-v1.xml`
  - Upstream: `https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/unstable/xdg-output/xdg-output-unstable-v1.xml`

Local server support in `Podish.Wayland` may intentionally implement only a subset of these protocols, but the XML files themselves should not be hand-trimmed or reordered. Unsupported requests should be handled in generated metadata or server/runtime code instead of modifying the vendored protocol definitions.
