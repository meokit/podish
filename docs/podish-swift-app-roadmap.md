# Podish Swift app roadmap

The SwiftUI app drives `Podish.Core` through the C ABI in
`Podish.Core.Native`. `Fiberish` owns Linux execution and syscall
semantics. Swift owns the UI, container selection, and terminal views.
[App package](../PodishApp/Package.swift) · [Native header](../Podish.Core.Native/podish.h)

## Current app and build

The app uses SwiftTerm to display guest terminal output. Its runtime
worker owns one native context and looks up containers by ID. It keeps
terminal sessions for attached containers. The app can pull and remove
images, list and inspect containers, start and stop them, and read their
logs. It shows image pull progress by consuming runtime events.
[Swift runtime worker](../PodishApp/Sources/Podish/PodishTerminalSession.swift) · [App store](../PodishApp/Sources/Podish/Store/PodishUiStore.swift)

The Swift package declares macOS 14 and iOS 16. Its XCFramework combines
NativeAOT static libraries and headers for macOS arm64, iOS arm64, and
the arm64 iOS simulator. Run these commands from the repository root on
macOS with Xcode installed.

```bash
dotnet workload restore Podish.Core.Native/Podish.Core.Native.csproj
bash Podish.Core.Native/scripts/publish-static.sh
swift build --package-path PodishApp
```

The publish script writes
`PodishApp/artifacts/podish-native/PodishCore.xcframework`. Use
`PodishApp/PodishApp.xcodeproj` and its shared `Podish` scheme for an iOS
app build. [Publish script](../Podish.Core.Native/scripts/publish-static.sh) · [Xcode scheme](../PodishApp/PodishApp.xcodeproj/xcshareddata/xcschemes/Podish.xcscheme)

## Current C ABI

| Area | Surface |
| --- | --- |
| Context | Create and destroy a context, configure working directory and logging at creation, query the last error. |
| Images | Pull, list, and remove images. |
| Containers | Create, open, start, stop with a signal and timeout, list, inspect, remove, and close handles. |
| Terminal | Attach to a running container with a terminal, read or write bytes, resize, and close the attachment handle. |
| Logs and events | Read logs as JSON, or poll the MsgPack event channel for logs and container state changes. |

The header exposes opaque `void *` handles. The implementation wraps
managed objects in `GCHandle`s and frees the handle in the corresponding
close or destroy call. Keep the context alive while using its handles,
and close each handle once. Swift queues native operations on its
runtime worker. The native context also has a single-reader command
queue for managed runtime operations.
[Native implementation](../Podish.Core.Native/PodishNativeApi.cs) · [Swift ABI bindings](../PodishApp/Sources/Podish/PodishNativeBridge.swift)

## Terminal ownership

The container owns the guest TTY. The app opens a terminal handle and
streams its bytes to SwiftTerm. Closing that attachment releases the
handle and its Swift output stream. Stopping the container uses the
separate container API. The guest TTY line discipline handles control
characters such as Ctrl-C and Ctrl-Z according to the guest's terminal
settings. It sends SIGWINCH to the foreground process group when the
window size changes.
[Native terminal operations](../Podish.Core.Native/PodishNativeApi.cs) · [TTY line discipline](../Fiberish.Core/VFS/TTY/TtyDiscipline.cs)

## Remaining work

| Area | Work |
| --- | --- |
| ABI | Add version negotiation and compatibility tests. Specify handle lifetime and which calls can overlap before promising a stable threading contract. |
| Terminal | Test attach, close, stop, output drain, control keys, and resize across container exit and restart. |
| Images | Add native load, save, import, and export calls. Extend the current pull progress reporting with cancellation. |
| Containers | Decide which standalone kill, wait, and exec operations belong in the native API. Exercise concurrent operations in one context and separate contexts sharing an image store. |
| Observability | Add filtering and bounded buffering for event and log consumers. Define callback and reentrancy rules if the ABI adds subscriptions. |
| iOS | Verify sandbox storage, backgrounding behavior, and app lifecycle on devices and simulators. |

Use the existing managed concurrency, restart, IPC, and terminal tests when
changing the native layer. Add tests for the public C ABI and an Xcode-driven
app smoke test as the API and UI contracts expand.
[Managed Podish tests](../Fiberish.Tests/Podish/)
