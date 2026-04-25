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

final class PodishTerminalView: TerminalView {
    override init(frame: CGRect) {
        // When created with .zero (e.g. from PodishTerminalSession.ensureTerminalView),
        // SwiftTerm falls back to its MINIMUM_COLS×MINIMUM_ROWS (2×1) buffer.
        // That causes the first shell prompt to wrap/truncate, leaving a stray
        // leading ‘/’ on its own line before the view gets its real layout.
        // Use a generous default frame so the terminal boots with a usable size.
        let effectiveFrame = frame.isEmpty
            ? CGRect(x: 0, y: 0, width: 600, height: 400)
            : frame
        super.init(frame: effectiveFrame)
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
    }
}
#endif
