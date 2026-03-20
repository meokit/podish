import Foundation

let POD_IPC_OP_POLL_EVENT: Int32 = 1
let POD_IPC_EVENT_NONE: Int32 = 0
let POD_IPC_EVENT_LOG_LINE: Int32 = 1
let POD_IPC_EVENT_CONTAINER_STATE_CHANGED: Int32 = 2

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

@_silgen_name("pod_ctx_call_msgpack")
func pod_ctx_call_msgpack(
    _ ctx: UnsafeMutableRawPointer?,
    _ op_id: Int32,
    _ args: UnsafePointer<UInt8>?,
    _ args_len: Int32,
    _ buffer: UnsafeMutablePointer<UInt8>?,
    _ capacity: Int32,
    _ out_len: UnsafeMutablePointer<Int32>?
) -> Int32

@_silgen_name("pod_image_pull")
func pod_image_pull(_ ctx: UnsafeMutableRawPointer?, _ image_ref_utf8: UnsafePointer<CChar>?) -> Int32

@_silgen_name("pod_image_list_json")
func pod_image_list_json(
    _ ctx: UnsafeMutableRawPointer?,
    _ buffer: UnsafeMutablePointer<UInt8>?,
    _ capacity: Int32,
    _ out_len: UnsafeMutablePointer<Int32>?
) -> Int32

@_silgen_name("pod_image_remove")
func pod_image_remove(
    _ ctx: UnsafeMutableRawPointer?,
    _ image_ref_utf8: UnsafePointer<CChar>?,
    _ force: Int32
) -> Int32

@_silgen_name("pod_container_create_json")
func pod_container_create_json(
    _ ctx: UnsafeMutableRawPointer?,
    _ run_spec_json_utf8: UnsafePointer<CChar>?,
    _ out_container: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_container_open")
func pod_container_open(
    _ ctx: UnsafeMutableRawPointer?,
    _ container_id_utf8: UnsafePointer<CChar>?,
    _ out_container: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_container_start")
func pod_container_start(_ container: UnsafeMutableRawPointer?) -> Int32

@_silgen_name("pod_container_stop")
func pod_container_stop(_ container: UnsafeMutableRawPointer?, _ signal: Int32, _ timeout_ms: Int32) -> Int32

@_silgen_name("pod_container_remove")
func pod_container_remove(_ container: UnsafeMutableRawPointer?, _ force: Int32) -> Int32

@_silgen_name("pod_container_list_json")
func pod_container_list_json(
    _ ctx: UnsafeMutableRawPointer?,
    _ buffer: UnsafeMutablePointer<UInt8>?,
    _ capacity: Int32,
    _ out_len: UnsafeMutablePointer<Int32>?
) -> Int32

@_silgen_name("pod_container_inspect_json")
func pod_container_inspect_json(
    _ container: UnsafeMutableRawPointer?,
    _ buffer: UnsafeMutablePointer<UInt8>?,
    _ capacity: Int32,
    _ out_len: UnsafeMutablePointer<Int32>?
) -> Int32

@_silgen_name("pod_container_close")
func pod_container_close(_ container: UnsafeMutableRawPointer?)

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

@_silgen_name("pod_logs_read_json")
func pod_logs_read_json(
    _ container: UnsafeMutableRawPointer?,
    _ cursor_utf8: UnsafePointer<CChar>?,
    _ follow: Int32,
    _ timeout_ms: Int32,
    _ buffer: UnsafeMutablePointer<UInt8>?,
    _ capacity: Int32,
    _ out_len: UnsafeMutablePointer<Int32>?
) -> Int32
