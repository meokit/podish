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
int pod_image_pull_async(void* ctx, const char* image_ref_utf8, void** out_job);
int pod_image_list_json(void* ctx, uint8_t* buffer, int capacity, int* out_len);
int pod_image_remove(void* ctx, const char* image_ref_utf8, int force);

// run_spec_json uses UTF-8 JSON matching PodishRunSpec fields.
int pod_container_run_json(void* ctx, const char* run_spec_json_utf8, int* exit_code);

int pod_container_create_json(void* ctx, const char* run_spec_json_utf8, void** out_container);
int pod_container_start_json(void* ctx, const char* run_spec_json_utf8, void** out_container);
int pod_container_open(void* ctx, const char* container_id_utf8, void** out_container);
int pod_container_start(void* container);
int pod_container_list_json(void* ctx, uint8_t* buffer, int capacity, int* out_len);
int pod_container_inspect_json(void* container, uint8_t* buffer, int capacity, int* out_len);
int pod_container_stop(void* container, int signal, int timeout_ms);
int pod_container_remove(void* container, int force);
void pod_container_close(void* container);
void pod_container_destroy(void* container);
int pod_container_write_stdin(void* container, const uint8_t* data, int len, int* written);
int pod_container_resize(void* container, uint16_t rows, uint16_t cols);
int pod_container_wait(void* container, int* exit_code);
int pod_container_wait_async(void* container, void** out_job);

int pod_terminal_attach(void* container, void** out_terminal);
int pod_terminal_write(void* terminal, const uint8_t* data, int len, int* written);
int pod_terminal_read(void* terminal, uint8_t* buffer, int capacity, int timeout_ms, int* out_read);
int pod_terminal_resize(void* terminal, uint16_t rows, uint16_t cols);
void pod_terminal_close(void* terminal);

int pod_events_read_json(void* ctx, const char* cursor_utf8, int timeout_ms, uint8_t* buffer, int capacity,
                         int* out_len);
int pod_logs_read_json(void* container, const char* cursor_utf8, int follow, int timeout_ms, uint8_t* buffer,
                       int capacity, int* out_len);

int pod_job_poll_json(void* job, uint8_t* buffer, int capacity, int* out_len);
void pod_job_destroy(void* job);

#ifdef __cplusplus
}
#endif

#endif
