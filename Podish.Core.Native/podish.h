#ifndef PODISH_H
#define PODISH_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    const char* work_dir_utf8;
    const char* log_level_utf8;
    const char* log_file_utf8;
} pod_ctx_options_t;

int pod_ctx_create(const pod_ctx_options_t* options, void** out_ctx);
void pod_ctx_destroy(void* ctx);

int pod_ctx_last_error(void* ctx, uint8_t* buffer, int capacity);

// MsgPack IPC (args is a msgpack array, response is msgpack payload).
#define POD_IPC_OP_POLL_EVENT 1
#define POD_IPC_EVENT_NONE 0
#define POD_IPC_EVENT_LOG_LINE 1
#define POD_IPC_EVENT_CONTAINER_STATE_CHANGED 2
int pod_ctx_call_msgpack(void* ctx, int op_id, const uint8_t* args, int args_len, uint8_t* buffer, int capacity,
                         int* out_len);

int pod_image_pull(void* ctx, const char* image_ref_utf8);
int pod_image_list_json(void* ctx, uint8_t* buffer, int capacity, int* out_len);
int pod_image_remove(void* ctx, const char* image_ref_utf8, int force);

int pod_container_create_json(void* ctx, const char* run_spec_json_utf8, void** out_container);
int pod_container_open(void* ctx, const char* container_id_utf8, void** out_container);
int pod_container_start(void* container);
int pod_container_list_json(void* ctx, uint8_t* buffer, int capacity, int* out_len);
int pod_container_inspect_json(void* container, uint8_t* buffer, int capacity, int* out_len);
int pod_container_stop(void* container, int signal, int timeout_ms);
int pod_container_remove(void* container, int force);
void pod_container_close(void* container);

int pod_terminal_attach(void* container, void** out_terminal);
int pod_terminal_write(void* terminal, const uint8_t* data, int len, int* written);
int pod_terminal_read(void* terminal, uint8_t* buffer, int capacity, int timeout_ms, int* out_read);
int pod_terminal_resize(void* terminal, uint16_t rows, uint16_t cols);
void pod_terminal_close(void* terminal);

int pod_logs_read_json(void* container, const char* cursor_utf8, int follow, int timeout_ms, uint8_t* buffer,
                       int capacity, int* out_len);

/* Embedded Debian Wayland Desktop API (tarfs + silkfs + ANGLE Metal) */
typedef void (*pod_text_input_cb_t)(void* user_data, int32_t active, int32_t x, int32_t y, int32_t w, int32_t h);
typedef void (*pod_cursor_cb_t)(void* user_data, int32_t cursor_shape);
typedef void (*pod_status_cb_t)(void* user_data, int32_t state, const char* message_utf8);
typedef int32_t (*pod_rasterize_text_cb_t)(void* user_data, const char* text_utf8, int32_t width, int32_t height,
                                           float font_size_px, uint32_t rgba, int32_t alignment, uint8_t* out_bgra,
                                           int32_t out_len);
typedef int32_t (*pod_rasterize_icon_cb_t)(void* user_data, const uint8_t* data, int32_t data_len, int32_t is_svg,
                                           int32_t size, uint8_t* out_bgra, int32_t out_len);
typedef void (*pod_vm_settings_cb_t)(void* user_data, int32_t memory_limit_mb, int32_t block_cache_mb);

typedef struct {
    const char* rootfs_store_dir_utf8;
    const char* state_dir_utf8;
    const char* angle_dir_utf8;
    const char* gles_guest_dir_utf8;
    const char* log_level_utf8;
    void* metal_layer;
    int32_t logical_width;
    int32_t logical_height;
    int32_t pixel_width;
    int32_t pixel_height;
    uint32_t scale_120;
    int32_t enable_gles;
    int32_t touch_mode;
    pod_text_input_cb_t on_text_input_changed;
    pod_cursor_cb_t on_cursor_changed;
    pod_status_cb_t on_status_changed;
    pod_rasterize_text_cb_t rasterize_text;
    pod_rasterize_icon_cb_t rasterize_icon;
    void* callback_user_data;
    int32_t vm_memory_limit_mb;
    int32_t vm_block_cache_mb;
    int32_t global_client_dpi;
    pod_vm_settings_cb_t on_vm_settings_changed;
} pod_desktop_options_t;

int pod_desktop_create(const pod_desktop_options_t* options, void** out_desktop);
int pod_desktop_start(void* desktop);
void pod_desktop_stop(void* desktop);
void pod_desktop_destroy(void* desktop);
int pod_desktop_last_error(void* desktop, uint8_t* buffer, int capacity);

void pod_desktop_resize(void* desktop, int32_t logical_width, int32_t logical_height, int32_t pixel_width,
                        int32_t pixel_height, uint32_t scale_120);
void pod_desktop_set_visible(void* desktop, int32_t visible);
void pod_desktop_pointer_motion(void* desktop, int32_t x, int32_t y, uint32_t timestamp_ms);
void pod_desktop_pointer_button(void* desktop, int32_t x, int32_t y, uint32_t button, int32_t pressed,
                                uint32_t timestamp_ms);
void pod_desktop_pointer_wheel(void* desktop, int32_t dx, int32_t dy, uint32_t timestamp_ms);
void pod_desktop_pointer_leave(void* desktop, uint32_t timestamp_ms);
void pod_desktop_key(void* desktop, uint32_t evdev_key, int32_t pressed, int32_t handled_by_ime, uint32_t timestamp_ms);
void pod_desktop_text_input(void* desktop, const char* text_utf8);
void pod_desktop_text_editing(void* desktop, const char* preedit_utf8, int32_t cursor_begin, int32_t cursor_end);
void pod_desktop_focus_changed(void* desktop, int32_t focused);
void pod_desktop_focus_launcher(void* desktop);
void pod_desktop_set_trackpad_mode(void* desktop, int32_t enabled);
void pod_desktop_set_client_scale(void* desktop, int32_t logical_width, int32_t logical_height, int32_t pixel_width,
                                  int32_t pixel_height, uint32_t scale_120, int32_t client_dpi);
void pod_desktop_set_global_dpi(void* desktop, int32_t global_dpi);
void pod_desktop_reload_config(void* desktop);
void pod_desktop_nav_action(void* desktop, int32_t action);
int pod_desktop_reset_overlay(const char* state_dir_utf8);

#ifdef __cplusplus
}
#endif

#endif