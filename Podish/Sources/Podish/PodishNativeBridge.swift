import Foundation

struct PodCtxOptionsNative {
    var work_dir_utf8: UnsafePointer<CChar>?
    var log_level_utf8: UnsafePointer<CChar>?
    var log_file_utf8: UnsafePointer<CChar>?
}

@_silgen_name("pod_ctx_create")
func pod_ctx_create(
    _ options: UnsafePointer<PodCtxOptionsNative>?,
    _ out_ctx: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_ctx_destroy")
func pod_ctx_destroy(_ ctx: UnsafeMutableRawPointer?)

@_silgen_name("pod_ctx_last_error")
func pod_ctx_last_error(_ ctx: UnsafeMutableRawPointer?, _ buffer: UnsafeMutablePointer<UInt8>?, _ capacity: Int32) -> Int32

@_silgen_name("pod_image_pull")
func pod_image_pull(_ ctx: UnsafeMutableRawPointer?, _ image_ref_utf8: UnsafePointer<CChar>?) -> Int32

@_silgen_name("pod_container_create_json")
func pod_container_create_json(
    _ ctx: UnsafeMutableRawPointer?,
    _ run_spec_json_utf8: UnsafePointer<CChar>?,
    _ out_container: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_container_start")
func pod_container_start(_ container: UnsafeMutableRawPointer?) -> Int32

@_silgen_name("pod_container_destroy")
func pod_container_destroy(_ container: UnsafeMutableRawPointer?)

@_silgen_name("pod_terminal_attach")
func pod_terminal_attach(
    _ container: UnsafeMutableRawPointer?,
    _ out_terminal: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_terminal_write")
func pod_terminal_write(
    _ terminal: UnsafeMutableRawPointer?,
    _ data: UnsafePointer<UInt8>?,
    _ len: Int32,
    _ written: UnsafeMutablePointer<Int32>?
) -> Int32

@_silgen_name("pod_terminal_read")
func pod_terminal_read(
    _ terminal: UnsafeMutableRawPointer?,
    _ buffer: UnsafeMutablePointer<UInt8>?,
    _ capacity: Int32,
    _ timeout_ms: Int32,
    _ out_read: UnsafeMutablePointer<Int32>?
) -> Int32

@_silgen_name("pod_terminal_resize")
func pod_terminal_resize(_ terminal: UnsafeMutableRawPointer?, _ rows: UInt16, _ cols: UInt16) -> Int32

@_silgen_name("pod_terminal_close")
func pod_terminal_close(_ terminal: UnsafeMutableRawPointer?)
