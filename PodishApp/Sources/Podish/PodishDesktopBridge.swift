import CoreGraphics
import CoreText
import Foundation
import ImageIO

// MARK: - C ABI Declarations (Podish.Core.Native)

typealias PodTextInputCallback = @convention(c) (UnsafeMutableRawPointer?, Int32, Int32, Int32, Int32, Int32) -> Void
typealias PodCursorCallback = @convention(c) (UnsafeMutableRawPointer?, Int32) -> Void
typealias PodStatusCallback = @convention(c) (UnsafeMutableRawPointer?, Int32, UnsafePointer<CChar>?) -> Void
typealias PodRasterizeTextCallback = @convention(c) (
    UnsafeMutableRawPointer?,
    UnsafePointer<CChar>?,
    Int32, Int32,
    Float,
    UInt32,
    Int32,
    UnsafeMutablePointer<UInt8>?,
    Int32
) -> Int32
typealias PodRasterizeIconCallback = @convention(c) (
    UnsafeMutableRawPointer?,
    UnsafePointer<UInt8>?,
    Int32,
    Int32,
    Int32,
    UnsafeMutablePointer<UInt8>?,
    Int32
) -> Int32
typealias PodVmSettingsCallback = @convention(c) (UnsafeMutableRawPointer?, Int32, Int32) -> Void

struct PodDesktopOptionsNative {
    var rootfs_store_dir_utf8: UnsafePointer<CChar>?
    var state_dir_utf8: UnsafePointer<CChar>?
    var angle_dir_utf8: UnsafePointer<CChar>?
    var gles_guest_dir_utf8: UnsafePointer<CChar>?
    var log_level_utf8: UnsafePointer<CChar>?
    var metal_layer: UnsafeMutableRawPointer?
    var logical_width: Int32
    var logical_height: Int32
    var pixel_width: Int32
    var pixel_height: Int32
    var scale_120: UInt32
    var enable_gles: Int32
    var touch_mode: Int32
    var on_text_input_changed: PodTextInputCallback?
    var on_cursor_changed: PodCursorCallback?
    var on_status_changed: PodStatusCallback?
    var rasterize_text: PodRasterizeTextCallback?
    var rasterize_icon: PodRasterizeIconCallback?
    var callback_user_data: UnsafeMutableRawPointer?
    var vm_memory_limit_mb: Int32
    var vm_block_cache_mb: Int32
    var global_client_dpi: Int32
    var on_vm_settings_changed: PodVmSettingsCallback?
}

@_silgen_name("pod_desktop_create")
func pod_desktop_create(
    _ options: UnsafePointer<PodDesktopOptionsNative>?,
    _ out_desktop: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_desktop_start")
func pod_desktop_start(_ desktop: UnsafeMutableRawPointer?) -> Int32

@_silgen_name("pod_desktop_stop")
func pod_desktop_stop(_ desktop: UnsafeMutableRawPointer?)

@_silgen_name("pod_desktop_destroy")
func pod_desktop_destroy(_ desktop: UnsafeMutableRawPointer?)

@_silgen_name("pod_desktop_last_error")
func pod_desktop_last_error(
    _ desktop: UnsafeMutableRawPointer?,
    _ buffer: UnsafeMutablePointer<UInt8>?,
    _ capacity: Int32
) -> Int32

@_silgen_name("pod_desktop_resize")
func pod_desktop_resize(
    _ desktop: UnsafeMutableRawPointer?,
    _ logical_width: Int32,
    _ logical_height: Int32,
    _ pixel_width: Int32,
    _ pixel_height: Int32,
    _ scale_120: UInt32
)

@_silgen_name("pod_desktop_set_visible")
func pod_desktop_set_visible(_ desktop: UnsafeMutableRawPointer?, _ visible: Int32)

@_silgen_name("pod_desktop_pointer_motion")
func pod_desktop_pointer_motion(_ desktop: UnsafeMutableRawPointer?, _ x: Int32, _ y: Int32, _ timestamp_ms: UInt32)

@_silgen_name("pod_desktop_pointer_button")
func pod_desktop_pointer_button(
    _ desktop: UnsafeMutableRawPointer?,
    _ x: Int32,
    _ y: Int32,
    _ button: UInt32,
    _ pressed: Int32,
    _ timestamp_ms: UInt32
)

@_silgen_name("pod_desktop_pointer_wheel")
func pod_desktop_pointer_wheel(_ desktop: UnsafeMutableRawPointer?, _ dx: Int32, _ dy: Int32, _ timestamp_ms: UInt32)

@_silgen_name("pod_desktop_pointer_leave")
func pod_desktop_pointer_leave(_ desktop: UnsafeMutableRawPointer?, _ timestamp_ms: UInt32)

@_silgen_name("pod_desktop_key")
func pod_desktop_key(
    _ desktop: UnsafeMutableRawPointer?,
    _ evdev_key: UInt32,
    _ pressed: Int32,
    _ handled_by_ime: Int32,
    _ timestamp_ms: UInt32
)

@_silgen_name("pod_desktop_text_input")
func pod_desktop_text_input(_ desktop: UnsafeMutableRawPointer?, _ text_utf8: UnsafePointer<CChar>?)

@_silgen_name("pod_desktop_text_editing")
func pod_desktop_text_editing(
    _ desktop: UnsafeMutableRawPointer?,
    _ preedit_utf8: UnsafePointer<CChar>?,
    _ cursor_begin: Int32,
    _ cursor_end: Int32
)

@_silgen_name("pod_desktop_focus_changed")
func pod_desktop_focus_changed(_ desktop: UnsafeMutableRawPointer?, _ focused: Int32)

@_silgen_name("pod_desktop_focus_launcher")
func pod_desktop_focus_launcher(_ desktop: UnsafeMutableRawPointer?)

@_silgen_name("pod_desktop_set_trackpad_mode")
func pod_desktop_set_trackpad_mode(_ desktop: UnsafeMutableRawPointer?, _ enabled: Int32)

@_silgen_name("pod_desktop_set_client_scale")
func pod_desktop_set_client_scale(
    _ desktop: UnsafeMutableRawPointer?,
    _ logical_width: Int32,
    _ logical_height: Int32,
    _ pixel_width: Int32,
    _ pixel_height: Int32,
    _ scale_120: UInt32,
    _ client_dpi: Int32
)

@_silgen_name("pod_desktop_set_global_dpi")
func pod_desktop_set_global_dpi(_ desktop: UnsafeMutableRawPointer?, _ global_dpi: Int32)

@_silgen_name("pod_desktop_reload_config")
func pod_desktop_reload_config(_ desktop: UnsafeMutableRawPointer?)

@_silgen_name("pod_desktop_nav_action")
func pod_desktop_nav_action(_ desktop: UnsafeMutableRawPointer?, _ action: Int32)

@_silgen_name("pod_desktop_reset_overlay")
func pod_desktop_reset_overlay(_ state_dir_utf8: UnsafePointer<CChar>?) -> Int32

// MARK: - CoreGraphics / CoreText Compositor Rasterizers (Thread-safe C callbacks)

let podishRasterizeTextCallback: PodRasterizeTextCallback = {
    _, textPtr, width, height, fontSizePx, rgba, alignment, outBgra, outLen in
    guard let textPtr, let outBgra, width > 0, height > 0 else { return 0 }
    let w = Int(width)
    let h = Int(height)
    let required = w * h * 4
    guard Int(outLen) >= required else { return 0 }

    let text = String(cString: textPtr)
    if text.isEmpty { return 0 }

    memset(outBgra, 0, required)
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    let bitmapInfo = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
    guard let ctx = CGContext(
        data: outBgra,
        width: w,
        height: h,
        bitsPerComponent: 8,
        bytesPerRow: w * 4,
        space: colorSpace,
        bitmapInfo: bitmapInfo
    ) else { return 0 }

    ctx.setAllowsAntialiasing(true)
    ctx.setShouldAntialias(true)
    ctx.setAllowsFontSmoothing(true)

    let r = CGFloat((rgba >> 24) & 0xFF) / 255.0
    let g = CGFloat((rgba >> 16) & 0xFF) / 255.0
    let b = CGFloat((rgba >> 8) & 0xFF) / 255.0
    let a = CGFloat(rgba & 0xFF) / 255.0
    let cgColor = CGColor(colorSpace: colorSpace, components: [r, g, b, a])!
    let font = CTFontCreateUIFontForLanguage(.system, CGFloat(max(8.0, fontSizePx)), nil)
        ?? CTFontCreateWithName("HelveticaNeue" as CFString, CGFloat(max(8.0, fontSizePx)), nil)

    let attrs: [NSAttributedString.Key: Any] = [
        NSAttributedString.Key(kCTFontAttributeName as String): font,
        NSAttributedString.Key(kCTForegroundColorAttributeName as String): cgColor
    ]
    let attrStr = NSAttributedString(string: text, attributes: attrs)
    var line = CTLineCreateWithAttributedString(attrStr)

    var ascent: CGFloat = 0
    var descent: CGFloat = 0
    var leading: CGFloat = 0
    var lineWidth = CGFloat(CTLineGetTypographicBounds(line, &ascent, &descent, &leading))

    if lineWidth > CGFloat(w) {
        let token = CTLineCreateWithAttributedString(NSAttributedString(string: "…", attributes: attrs))
        if let truncated = CTLineCreateTruncatedLine(line, Double(w), .end, token) {
            line = truncated
            lineWidth = CGFloat(CTLineGetTypographicBounds(line, &ascent, &descent, &leading))
        }
    }

    let x: CGFloat
    switch alignment {
    case 1: x = max(0, (CGFloat(w) - lineWidth) * 0.5)
    case 2: x = max(0, CGFloat(w) - lineWidth)
    default: x = 0
    }
    let y = (CGFloat(h) - (ascent + descent)) * 0.5 + descent

    ctx.textMatrix = .identity
    ctx.textPosition = CGPoint(x: floor(x), y: floor(y))
    CTLineDraw(line, ctx)
    return 1
}

let podishRasterizeIconCallback: PodRasterizeIconCallback = {
    _, dataPtr, dataLen, isSvg, size, outBgra, outLen in
    guard let dataPtr, let outBgra, dataLen > 0, size > 0, isSvg == 0 else { return 0 }
    let s = Int(size)
    let required = s * s * 4
    guard Int(outLen) >= required else { return 0 }

    let data = Data(bytes: dataPtr, count: Int(dataLen)) as CFData
    guard let source = CGImageSourceCreateWithData(data, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
        return 0
    }

    memset(outBgra, 0, required)
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    let bitmapInfo = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
    guard let ctx = CGContext(
        data: outBgra,
        width: s,
        height: s,
        bitsPerComponent: 8,
        bytesPerRow: s * 4,
        space: colorSpace,
        bitmapInfo: bitmapInfo
    ) else { return 0 }

    ctx.interpolationQuality = .high
    let iw = CGFloat(image.width)
    let ih = CGFloat(image.height)
    let scale = CGFloat(s) / max(1, max(iw, ih))
    let dw = iw * scale
    let dh = ih * scale
    let rect = CGRect(x: (CGFloat(s) - dw) * 0.5, y: (CGFloat(s) - dh) * 0.5, width: dw, height: dh)
    ctx.draw(image, in: rect)
    return 1
}

// MARK: - Linux Evdev Key Mapping

enum PodishEvdevKeyMap {
    #if os(macOS)
    static func fromMacKeyCode(_ keyCode: UInt16) -> UInt32? {
        switch keyCode {
        case 0x00: return 30 // A
        case 0x01: return 31 // S
        case 0x02: return 32 // D
        case 0x03: return 33 // F
        case 0x04: return 35 // H
        case 0x05: return 34 // G
        case 0x06: return 44 // Z
        case 0x07: return 45 // X
        case 0x08: return 46 // C
        case 0x09: return 47 // V
        case 0x0B: return 48 // B
        case 0x0C: return 16 // Q
        case 0x0D: return 17 // W
        case 0x0E: return 18 // E
        case 0x0F: return 19 // R
        case 0x10: return 21 // Y
        case 0x11: return 20 // T
        case 0x12: return 2  // 1
        case 0x13: return 3  // 2
        case 0x14: return 4  // 3
        case 0x15: return 5  // 4
        case 0x16: return 7  // 6
        case 0x17: return 6  // 5
        case 0x18: return 13 // =
        case 0x19: return 10 // 9
        case 0x1A: return 8  // 7
        case 0x1B: return 12 // -
        case 0x1C: return 9  // 8
        case 0x1D: return 11 // 0
        case 0x1E: return 27 // ]
        case 0x1F: return 24 // O
        case 0x20: return 22 // U
        case 0x21: return 26 // [
        case 0x22: return 23 // I
        case 0x23: return 25 // P
        case 0x24: return 28 // Return
        case 0x25: return 38 // L
        case 0x26: return 36 // J
        case 0x27: return 40 // '
        case 0x28: return 37 // K
        case 0x29: return 39 // ;
        case 0x2A: return 43 // backslash
        case 0x2B: return 51 // ,
        case 0x2C: return 53 // /
        case 0x2D: return 49 // N
        case 0x2E: return 50 // M
        case 0x2F: return 52 // .
        case 0x30: return 15 // Tab
        case 0x31: return 57 // Space
        case 0x32: return 41 // `
        case 0x33: return 14 // Backspace
        case 0x35: return 1  // Escape
        case 0x36: return 126 // Right Super
        case 0x37: return 125 // Left Super
        case 0x38: return 42 // Left Shift
        case 0x39: return 58 // Caps Lock
        case 0x3A: return 56 // Left Alt
        case 0x3B: return 29 // Left Ctrl
        case 0x3C: return 54 // Right Shift
        case 0x3D: return 100 // Right Alt
        case 0x3E: return 97 // Right Ctrl
        case 0x72: return 110 // Insert
        case 0x73: return 102 // Home
        case 0x74: return 104 // PageUp
        case 0x75: return 111 // Delete
        case 0x77: return 107 // End
        case 0x79: return 109 // PageDown
        case 0x7A: return 59 // F1
        case 0x78: return 60 // F2
        case 0x63: return 61 // F3
        case 0x76: return 62 // F4
        case 0x60: return 63 // F5
        case 0x61: return 64 // F6
        case 0x62: return 65 // F7
        case 0x64: return 66 // F8
        case 0x65: return 67 // F9
        case 0x6D: return 68 // F10
        case 0x67: return 87 // F11
        case 0x6F: return 88 // F12
        case 0x7B: return 105 // Left
        case 0x7C: return 106 // Right
        case 0x7D: return 108 // Down
        case 0x7E: return 103 // Up
        default: return nil
        }
    }
    #endif

    static func fromHidUsage(_ usage: Int) -> UInt32? {
        switch usage {
        case 0x04: return 30 // A
        case 0x05: return 48 // B
        case 0x06: return 46 // C
        case 0x07: return 32 // D
        case 0x08: return 18 // E
        case 0x09: return 33 // F
        case 0x0A: return 34 // G
        case 0x0B: return 35 // H
        case 0x0C: return 23 // I
        case 0x0D: return 36 // J
        case 0x0E: return 37 // K
        case 0x0F: return 38 // L
        case 0x10: return 50 // M
        case 0x11: return 49 // N
        case 0x12: return 24 // O
        case 0x13: return 25 // P
        case 0x14: return 16 // Q
        case 0x15: return 19 // R
        case 0x16: return 31 // S
        case 0x17: return 20 // T
        case 0x18: return 22 // U
        case 0x19: return 47 // V
        case 0x1A: return 17 // W
        case 0x1B: return 45 // X
        case 0x1C: return 21 // Y
        case 0x1D: return 44 // Z
        case 0x1E: return 2  // 1
        case 0x1F: return 3  // 2
        case 0x20: return 4  // 3
        case 0x21: return 5  // 4
        case 0x22: return 6  // 5
        case 0x23: return 7  // 6
        case 0x24: return 8  // 7
        case 0x25: return 9  // 8
        case 0x26: return 10 // 9
        case 0x27: return 11 // 0
        case 0x28: return 28 // Return
        case 0x29: return 1  // Escape
        case 0x2A: return 14 // Backspace
        case 0x2B: return 15 // Tab
        case 0x2C: return 57 // Space
        case 0x2D: return 12 // -
        case 0x2E: return 13 // =
        case 0x2F: return 26 // [
        case 0x30: return 27 // ]
        case 0x31: return 43 // backslash
        case 0x33: return 39 // ;
        case 0x34: return 40 // '
        case 0x35: return 41 // `
        case 0x36: return 51 // ,
        case 0x37: return 52 // .
        case 0x38: return 53 // /
        case 0x39: return 58 // Caps Lock
        case 0x3A: return 59 // F1
        case 0x3B: return 60 // F2
        case 0x3C: return 61 // F3
        case 0x3D: return 62 // F4
        case 0x3E: return 63 // F5
        case 0x3F: return 64 // F6
        case 0x40: return 65 // F7
        case 0x41: return 66 // F8
        case 0x42: return 67 // F9
        case 0x43: return 68 // F10
        case 0x44: return 87 // F11
        case 0x45: return 88 // F12
        case 0x49: return 110 // Insert
        case 0x4A: return 102 // Home
        case 0x4B: return 104 // PageUp
        case 0x4C: return 111 // Delete
        case 0x4D: return 107 // End
        case 0x4E: return 109 // PageDown
        case 0x4F: return 106 // Right
        case 0x50: return 105 // Left
        case 0x51: return 108 // Down
        case 0x52: return 103 // Up
        case 0xE0: return 29  // Left Ctrl
        case 0xE1: return 42  // Left Shift
        case 0xE2: return 56  // Left Alt
        case 0xE3: return 125 // Left Super
        case 0xE4: return 97  // Right Ctrl
        case 0xE5: return 54  // Right Shift
        case 0xE6: return 100 // Right Alt
        case 0xE7: return 126 // Right Super
        default: return nil
        }
    }

    static func asciiKeyStroke(_ ch: Character) -> (evdev: UInt32, shift: Bool)? {
        switch ch {
        case "a"..."z":
            let map: [Character: UInt32] = [
                "a": 30, "b": 48, "c": 46, "d": 32, "e": 18, "f": 33, "g": 34, "h": 35,
                "i": 23, "j": 36, "k": 37, "l": 38, "m": 50, "n": 49, "o": 24, "p": 25,
                "q": 16, "r": 19, "s": 31, "t": 20, "u": 22, "v": 47, "w": 17, "x": 45,
                "y": 21, "z": 44
            ]
            return map[ch].map { ($0, false) }
        case "A"..."Z":
            return asciiKeyStroke(Character(ch.lowercased())).map { ($0.evdev, true) }
        case "1": return (2, false)
        case "2": return (3, false)
        case "3": return (4, false)
        case "4": return (5, false)
        case "5": return (6, false)
        case "6": return (7, false)
        case "7": return (8, false)
        case "8": return (9, false)
        case "9": return (10, false)
        case "0": return (11, false)
        case "!": return (2, true)
        case "@": return (3, true)
        case "#": return (4, true)
        case "$": return (5, true)
        case "%": return (6, true)
        case "^": return (7, true)
        case "&": return (8, true)
        case "*": return (9, true)
        case "(": return (10, true)
        case ")": return (11, true)
        case "\n", "\r": return (28, false)
        case "\t": return (15, false)
        case " ": return (57, false)
        case "-": return (12, false)
        case "_": return (12, true)
        case "=": return (13, false)
        case "+": return (13, true)
        case "[": return (26, false)
        case "{": return (26, true)
        case "]": return (27, false)
        case "}": return (27, true)
        case "\\": return (43, false)
        case "|": return (43, true)
        case ";": return (39, false)
        case ":": return (39, true)
        case "'": return (40, false)
        case "\"": return (40, true)
        case "`": return (41, false)
        case "~": return (41, true)
        case ",": return (51, false)
        case "<": return (51, true)
        case ".": return (52, false)
        case ">": return (52, true)
        case "/": return (53, false)
        case "?": return (53, true)
        default: return nil
        }
    }
}
