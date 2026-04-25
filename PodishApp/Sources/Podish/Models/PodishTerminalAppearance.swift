import SwiftUI
@preconcurrency import SwiftTerm

#if os(macOS)
import AppKit
typealias PlatformColor = NSColor
#else
import UIKit
typealias PlatformColor = UIColor
#endif

@MainActor
final class PodishTerminalAppearance: ObservableObject {
    struct Theme: Equatable {
        let name: String
        let ansi: [ThemeColor]
        let background: ThemeColor
        let foreground: ThemeColor
        let cursor: ThemeColor

        static let elemental = Theme(
            name: "Elemental",
            ansi: [
                ThemeColor(hex: "#3C3C30"),
                ThemeColor(hex: "#98290F"),
                ThemeColor(hex: "#479A43"),
                ThemeColor(hex: "#7F7111"),
                ThemeColor(hex: "#497F7D"),
                ThemeColor(hex: "#7F4E2F"),
                ThemeColor(hex: "#387F58"),
                ThemeColor(hex: "#807974"),
                ThemeColor(hex: "#555445"),
                ThemeColor(hex: "#E0502A"),
                ThemeColor(hex: "#61E070"),
                ThemeColor(hex: "#D69927"),
                ThemeColor(hex: "#79D9D9"),
                ThemeColor(hex: "#CD7C54"),
                ThemeColor(hex: "#59D599"),
                ThemeColor(hex: "#FFF1E9")
            ],
            background: ThemeColor(hex: "#22211D"),
            foreground: ThemeColor(hex: "#807A74"),
            cursor: ThemeColor(hex: "#807A74")
        )
    }

    struct ThemeColor: Equatable {
        let red: UInt8
        let green: UInt8
        let blue: UInt8

        init(hex: String) {
            let sanitized = hex.trimmingCharacters(in: CharacterSet.alphanumerics.inverted)
            guard sanitized.count == 6, let value = UInt32(sanitized, radix: 16) else {
                self.red = 0
                self.green = 0
                self.blue = 0
                return
            }

            self.red = UInt8((value >> 16) & 0xFF)
            self.green = UInt8((value >> 8) & 0xFF)
            self.blue = UInt8(value & 0xFF)
        }

        var terminalColor: SwiftTerm.Color {
            SwiftTerm.Color(
                red: UInt16(red) * 257,
                green: UInt16(green) * 257,
                blue: UInt16(blue) * 257
            )
        }

        var platformColor: PlatformColor {
            PlatformColor(
                red: CGFloat(red) / 255.0,
                green: CGFloat(green) / 255.0,
                blue: CGFloat(blue) / 255.0,
                alpha: 1.0
            )
        }

        var swiftUIColor: SwiftUI.Color {
            SwiftUI.Color(
                red: Double(red) / 255.0,
                green: Double(green) / 255.0,
                blue: Double(blue) / 255.0
            )
        }
    }

    @Published var theme: Theme

    init(theme: Theme = .elemental) {
        self.theme = theme
    }

    var terminalBackgroundColor: SwiftUI.Color {
        theme.background.swiftUIColor
    }

    func apply(to terminalView: TerminalView) {
        let terminal = terminalView.getTerminal()
        terminal.installPalette(colors: theme.ansi.map(\.terminalColor))
        terminal.foregroundColor = theme.foreground.terminalColor
        terminal.backgroundColor = theme.background.terminalColor
        terminal.cursorColor = theme.cursor.terminalColor

        terminalView.nativeForegroundColor = theme.foreground.platformColor
        terminalView.nativeBackgroundColor = theme.background.platformColor
        terminalView.caretColor = theme.cursor.platformColor
        terminalView.caretTextColor = theme.background.platformColor
    }
}
