# External Device Registration Interface, SDL Example

This document plans the host-side interface for registering external devices into a frontend.
SDL is used as the concrete example, but the contract should stay host-agnostic.

## Goal

- [ ] Let Fiberish or Podish register host-facing devices through a single stable contract.
- [ ] Make SDL one implementation of that contract.
- [ ] Keep the API flexible enough for later SwiftUI, macOS, iOS, or headless backends.

## Design Principles

- [ ] Separate device registration from device rendering.
- [ ] Separate event ingestion from guest input injection.
- [ ] Keep UI-thread-only APIs isolated from runtime-thread-safe APIs.
- [ ] Make every registration explicit and reference-counted or disposable.
- [ ] Avoid global singleton device state.
- [ ] Allow the frontend to refuse a device cleanly if the backend cannot support it.

## What Counts As An "External Device"

- [ ] Display / scanout surface
- [ ] Keyboard input
- [ ] Mouse / pointer input
- [ ] Touch / tablet input
- [ ] Clipboard bridge
- [ ] Optional audio sink or source later
- [ ] Optional gamepad later

## Proposed Layers

### 1. Host-Agnostic Registry

- [ ] Owns device IDs and lifetime.
- [ ] Accepts registration requests from runtime code.
- [ ] Returns handles that can be used to update or destroy the device.
- [ ] Exposes capability discovery before creation.

### 2. Frontend Backend

- [ ] Implements the registry for a specific UI toolkit.
- [ ] Creates windows, surfaces, textures, and event pumps.
- [ ] Owns the platform main thread requirements.
- [ ] Converts host events into generic input events.

### 3. Guest Device Adapters

- [ ] Translate host-side device actions into Fiberish guest operations.
- [ ] Translate guest-side output into host-side presentation updates.
- [ ] Keep transport details out of UI code.

## Proposed Core Interface Shape

### Registry

- [ ] `RegisterDisplay(...)`
- [ ] `RegisterKeyboard(...)`
- [ ] `RegisterPointer(...)`
- [ ] `RegisterTouch(...)`
- [ ] `RegisterClipboard(...)`
- [ ] `QueryCapabilities()`
- [ ] `ShutdownDevice(deviceId)`
- [ ] `ShutdownAll()`

### Display Device

- [ ] `CreateSurface(width, height, format)`
- [ ] `PresentFrame(framebufferHandle, damageRects)`
- [ ] `Resize(width, height)`
- [ ] `SetTitle(title)`
- [ ] `SetVisible(visible)`
- [ ] `Close()`

### Input Device

- [ ] `InjectKeyDown(keycode, modifiers)`
- [ ] `InjectKeyUp(keycode, modifiers)`
- [ ] `InjectText(text)`
- [ ] `InjectPointerMove(x, y)`
- [ ] `InjectPointerButton(button, pressed)`
- [ ] `InjectPointerScroll(dx, dy)`
- [ ] `InjectTouch(...)`

## SDL As The Example Backend

### SDL responsibilities

- [ ] Initialize the video subsystem.
- [ ] Create one window per display device or one shared host surface per session.
- [ ] Create a renderer or texture pipeline for frame uploads.
- [ ] Pump SDL events on the UI thread.
- [ ] Map SDL keyboard/mouse/touch events into generic device events.
- [ ] Resize the host surface when the guest mode changes.

### SDL constraints

- [ ] SDL window and renderer operations should stay on the SDL thread.
- [ ] Frame updates from runtime threads should go through a thread-safe queue.
- [ ] Guest writes should not call SDL APIs directly.
- [ ] The registry should serialize creation and teardown so SDL objects are not used after destroy.

### SDL device mapping example

- [ ] `Display` -> SDL window + renderer + texture
- [ ] `Keyboard` -> SDL keyboard event translation
- [ ] `Pointer` -> SDL mouse event translation
- [ ] `Touch` -> SDL touch event translation if the platform supports it
- [ ] `Clipboard` -> SDL clipboard bridge if enabled

## Recommended Device Lifecycle

1. [ ] Runtime asks the registry to create a display device.
2. [ ] Backend allocates a window and returns a device handle.
3. [ ] Guest display state binds to that handle.
4. [ ] Guest commits a framebuffer or frame upload.
5. [ ] Frontend presents the pixels.
6. [ ] Host events are translated back into guest input.
7. [ ] Device is closed on guest shutdown or frontend teardown.

## Threading Model

- [ ] UI thread owns SDL init, event pump, window, texture, and renderer objects.
- [ ] Runtime thread owns guest-facing state and can enqueue frame updates.
- [ ] A shared queue or channel moves presentation requests to the UI thread.
- [ ] A separate queue or callback moves host input events into the guest runtime.
- [ ] Close and shutdown must be idempotent.

## Error Handling

- [ ] Return a typed failure if the backend is unavailable.
- [ ] Return a typed failure if a requested surface format is unsupported.
- [ ] Return a typed failure if the host window cannot be created.
- [ ] Preserve the original backend error message for diagnostics.
- [ ] Distinguish between transient failure and permanent unsupported capability.

## Suggested Type Model

### Device Descriptor

- [ ] `DeviceKind`
- [ ] `DeviceName`
- [ ] `InitialSize`
- [ ] `PreferredFormat`
- [ ] `ThreadAffinity`
- [ ] `Capabilities`

### Device Handle

- [ ] Opaque handle or ID
- [ ] `Dispose()` / `Close()`
- [ ] `Present()` for display
- [ ] `SendInput()` for input devices

### Event Payloads

- [ ] `ResizeRequested`
- [ ] `FramePresented`
- [ ] `KeyEvent`
- [ ] `PointerEvent`
- [ ] `ClipboardEvent`

## Fiberish / Podish Touchpoints

- [ ] Add a host-device registry in the app layer, not in low-level VFS code.
- [ ] Bind guest display state to a registry device handle.
- [ ] Let the registry create or destroy SDL windows based on guest device lifetime.
- [ ] Keep terminal bridge and display bridge separate.

Likely files:

- `Podish.Core/PodishContext.cs`
- new `Podish.Core/HostDeviceRegistry.cs`
- new `Podish.Core/HostDevices/*.cs`
- future `Podish` UI integration layer

## SDL Implementation Phases

### Phase A: Minimal Window Device

- [ ] Create a window.
- [ ] Present a solid color test frame.
- [ ] Tear down cleanly.

### Phase B: Guest Scanout Device

- [ ] Upload framebuffer pixels to the window.
- [ ] Resize the window on guest mode changes.
- [ ] Support one display surface per guest session.

### Phase C: Input Bridge

- [ ] Translate keyboard and mouse input into guest events.
- [ ] Support focus and grab behavior.
- [ ] Add basic clipboard support.

### Phase D: Multi-Device Expansion

- [ ] Add multiple displays if needed.
- [ ] Add touch and tablet input.
- [ ] Add audio only if a guest workload needs it.

## Decisions To Make Before Coding

- [ ] Do we want one registry per app session or one per guest container?
- [ ] Do we want one SDL window per guest display or one shared window with tabs/panels?
- [ ] Should input devices be auto-created with display devices, or registered independently?
- [ ] Should the registry live in `Podish.Core` or in a separate host-services layer?
- [ ] Should device registration be synchronous or async?

