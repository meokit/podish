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

int pod_image_pull(void* ctx, const char* image_ref_utf8);

// run_spec_json uses UTF-8 JSON matching PodishRunSpec fields.
int pod_container_run_json(void* ctx, const char* run_spec_json_utf8, int* exit_code);

typedef void (*pod_terminal_output_cb)(void* user_data, int stream_kind, const uint8_t* data, int len);

int pod_container_start_json(void* ctx, const char* run_spec_json_utf8, void** out_container);
void pod_container_destroy(void* container);
int pod_container_set_output_callback(void* container, pod_terminal_output_cb callback, void* user_data);
int pod_container_write_stdin(void* container, const uint8_t* data, int len, int* written);
int pod_container_resize(void* container, uint16_t rows, uint16_t cols);
int pod_container_wait(void* container, int* exit_code);

#ifdef __cplusplus
}
#endif

#endif
