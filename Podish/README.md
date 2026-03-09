# Podish

A minimal SwiftUI macOS app that embeds a `SwiftTerm` terminal view.

Current native artifacts are built for Apple Silicon (`osx-arm64`) on macOS.

## Open in Xcode

Open `/Users/jiangyiheng/repos/x86emu/Podish/Package.swift` in Xcode.

## Build from CLI

```bash
cd /Users/jiangyiheng/repos/x86emu
dotnet restore Fiberish.sln
cd /Users/jiangyiheng/repos/x86emu/Podish
bash ../Podish.Core.Native/scripts/publish-static.sh
swift build
```

The publish script builds all Apple slices (`osx-arm64`, `ios-arm64`, `iossimulator-arm64`) and produces:
`/Users/jiangyiheng/repos/x86emu/Podish/artifacts/podish-native/PodishCore.xcframework`

## Build iOS native artifacts

```bash
cd /Users/jiangyiheng/repos/x86emu/Podish
dotnet workload restore ../Podish.Core.Native/Podish.Core.Native.csproj
bash ../Podish.Core.Native/scripts/publish-static.sh
```

Then build for iPhone:

```bash
xcodebuild -scheme Podish -destination 'generic/platform=iOS' build
```

## Run from CLI

```bash
cd /Users/jiangyiheng/repos/x86emu/Podish
swift run Podish
```
