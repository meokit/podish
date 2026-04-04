import SwiftUI
@preconcurrency import SwiftTerm

#if os(macOS)
import AppKit

final class PodishTerminalView: TerminalView {
    // SwiftTerm currently returns NSRange.empty (0,0) for "no marked text",
    // which can trigger NSTextInput/XPC range warnings with IME.
    override func markedRange() -> NSRange {
        return NSRange(location: NSNotFound, length: 0)
    }
}
#else
import UIKit

final class PodishTerminalView: TerminalView {}
#endif
