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
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        if !isFirstResponder {
            PodishLog.ui("TerminalView touchesBegan requesting first responder")
            _ = becomeFirstResponder()
        }
        super.touchesBegan(touches, with: event)
    }

    override func becomeFirstResponder() -> Bool {
        PodishLog.ui("TerminalView becomeFirstResponder requested window=\(window != nil) firstResponder=\(isFirstResponder)")
        let response = super.becomeFirstResponder()
        PodishLog.ui("TerminalView becomeFirstResponder result=\(response) firstResponder=\(isFirstResponder)")
        return response
    }

    override func resignFirstResponder() -> Bool {
        PodishLog.ui("TerminalView resignFirstResponder requested")
        let response = super.resignFirstResponder()
        PodishLog.ui("TerminalView resignFirstResponder result=\(response)")
        return response
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        PodishLog.ui("TerminalView didMoveToWindow windowAttached=\(window != nil)")
    }
}
#endif
