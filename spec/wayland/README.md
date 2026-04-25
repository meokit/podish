These protocol XML files are vendored from upstream Wayland projects and are intended to stay as canonical protocol descriptions.

Sources:
- `wayland.xml`
  - Upstream: `https://cgit.freedesktop.org/wayland/wayland/plain/protocol/wayland.xml`
  - Retrieved: `2026-03-24`
- `xdg-shell.xml`
  - Upstream: `https://cgit.freedesktop.org/wayland/wayland-protocols/plain/stable/xdg-shell/xdg-shell.xml`
  - Retrieved: `2026-03-24`

Local server support in `Podish.Wayland` may intentionally implement only a subset of these protocols, but the XML files themselves should not be hand-trimmed or reordered. Unsupported requests should be handled in generated metadata or server/runtime code instead of modifying the vendored protocol definitions.
