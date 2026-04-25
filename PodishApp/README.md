# Podish

`Podish` is the SwiftUI frontend in this repository. It packages the native runtime as an Apple XCFramework and provides a terminal-style app shell on top of it.

## Open in Xcode

Open `PodishApp/Package.swift` in Xcode.

## Build from the repo root

Restore managed dependencies first:

```bash
dotnet restore Podish.slnx
```

Build the Apple XCFramework used by the app:

```bash
bash Podish.Core.Native/scripts/publish-static.sh
```

This produces:

```text
PodishApp/artifacts/podish-native/PodishCore.xcframework
```

Then build the Swift package:

```bash
cd Podish
swift build
```

## Build Apple native artifacts only

If you only want the native runtime slices:

```bash
dotnet workload restore Podish.Core.Native/Podish.Core.Native.csproj
bash Podish.Core.Native/scripts/publish-static.sh
```

The publish script builds:

- `osx-arm64`
- `ios-arm64`
- `iossimulator-arm64`

## Build for iPhone

```bash
cd Podish
xcodebuild -scheme Podish -destination 'generic/platform=iOS' build
```

## Run from CLI

```bash
cd Podish
swift run Podish
```
