# Podish

A minimal SwiftUI macOS app that embeds a `SwiftTerm` terminal view.

## Open in Xcode

Open `/Users/jiangyiheng/repos/x86emu/Podish/Package.swift` in Xcode.

## Build from CLI

```bash
cd /Users/jiangyiheng/repos/x86emu
dotnet restore Fiberish.sln
cd /Users/jiangyiheng/repos/x86emu/Podish
bash ../Podish.Core.Native/scripts/publish-static.sh osx-arm64 artifacts/podish-native/osx-arm64
swift build
```

The publish script will run `dotnet restore/publish` for `Podish.Core.Native` and stage outputs (including `PodishCore.framework`) at:
`/Users/jiangyiheng/repos/x86emu/Podish/artifacts/podish-native/osx-arm64`

## Build iOS native artifacts (arm64)

```bash
cd /Users/jiangyiheng/repos/x86emu/Podish
dotnet workload restore ../Podish.Core.Native/Podish.Core.Native.csproj
bash ../Podish.Core.Native/scripts/publish-static.sh ios-arm64 artifacts/podish-native/ios-arm64
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
