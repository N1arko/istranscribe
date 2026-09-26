#ifndef ISTRANSCRIBE_WHISPER_V1_H
#define ISTRANSCRIBE_WHISPER_V1_H

#include <stdint.h>

#if defined(_WIN32)
#  define ISTRANSCRIBE_WHISPER_CALL __cdecl
#  if defined(ISTRANSCRIBE_WHISPER_BUILD)
#    define ISTRANSCRIBE_WHISPER_API __declspec(dllexport)
#  else
#    define ISTRANSCRIBE_WHISPER_API __declspec(dllimport)
#  endif
#else
#  define ISTRANSCRIBE_WHISPER_CALL
#  define ISTRANSCRIBE_WHISPER_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * App-owned ABI boundary. Upstream whisper.cpp and ggml declarations never
 * cross this header.
 *
 * @spec spec://modules/app/FEAT-016-local-whisper-transcription#engine.implementation
 * @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
 */

#define ISTRANSCRIBE_WHISPER_ABI_VERSION_V1 UINT32_C(1)
#define ISTRANSCRIBE_WHISPER_SAMPLE_RATE_V1 UINT32_C(16000)
#define ISTRANSCRIBE_WHISPER_MAX_SAMPLES_V1 UINT64_C(4800000)

typedef int32_t istranscribe_whisper_result_v1;

#define ISTRANSCRIBE_WHISPER_OK_V1 INT32_C(0)
#define ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1 INT32_C(1)
#define ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1 INT32_C(2)
#define ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1 INT32_C(3)
#define ISTRANSCRIBE_WHISPER_MODEL_LOAD_FAILED_V1 INT32_C(4)
#define ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1 INT32_C(5)
#define ISTRANSCRIBE_WHISPER_CANCELLED_V1 INT32_C(6)
#define ISTRANSCRIBE_WHISPER_OUT_OF_RANGE_V1 INT32_C(7)
#define ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1 INT32_C(100)

typedef uint32_t istranscribe_whisper_backend_v1;

#define ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1 UINT32_C(1)
#define ISTRANSCRIBE_WHISPER_BACKEND_METAL_V1 UINT32_C(2)
#define ISTRANSCRIBE_WHISPER_BACKEND_VULKAN_V1 UINT32_C(3)

typedef struct istranscribe_whisper_utf8_view_v1 {
    uint32_t struct_size;
    uint32_t reserved;
    const uint8_t * data;
    uint64_t length;
} istranscribe_whisper_utf8_view_v1;

typedef struct istranscribe_whisper_backend_probe_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    istranscribe_whisper_backend_v1 requested_backend;
    uint32_t available;
    uint64_t memory_free_bytes;
    uint64_t memory_total_bytes;
    istranscribe_whisper_utf8_view_v1 device_name;
    istranscribe_whisper_utf8_view_v1 runtime_version;
    uint64_t reserved[4];
} istranscribe_whisper_backend_probe_v1;

typedef struct istranscribe_whisper_context_options_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    istranscribe_whisper_backend_v1 backend;
    uint32_t thread_count;
    istranscribe_whisper_utf8_view_v1 model_path;
    uint64_t reserved[4];
} istranscribe_whisper_context_options_v1;

typedef struct istranscribe_whisper_transcribe_options_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    const float * pcm_f32_16khz_mono;
    uint64_t sample_count;
    istranscribe_whisper_utf8_view_v1 language;
    uint64_t reserved[4];
} istranscribe_whisper_transcribe_options_v1;

typedef struct istranscribe_whisper_segment_v1 {
    uint32_t struct_size;
    uint32_t reserved;
    int64_t start_ms;
    int64_t end_ms;
    istranscribe_whisper_utf8_view_v1 text;
    uint64_t reserved_future[4];
} istranscribe_whisper_segment_v1;

typedef struct istranscribe_whisper_timings_v1 {
    uint32_t struct_size;
    uint32_t reserved;
    double sample_ms;
    double encode_ms;
    double decode_ms;
    double batch_ms;
    double prompt_ms;
    uint64_t reserved_future[4];
} istranscribe_whisper_timings_v1;

typedef struct istranscribe_whisper_context_info_v1 {
    uint32_t struct_size;
    uint32_t abi_version;
    istranscribe_whisper_backend_v1 resolved_backend;
    uint32_t reserved;
    istranscribe_whisper_utf8_view_v1 detected_language;
    istranscribe_whisper_utf8_view_v1 backend_device_name;
    uint64_t reserved_future[4];
} istranscribe_whisper_context_info_v1;

typedef struct istranscribe_whisper_error_v1 {
    uint32_t struct_size;
    istranscribe_whisper_result_v1 code;
    istranscribe_whisper_utf8_view_v1 message;
    uint64_t reserved[4];
} istranscribe_whisper_error_v1;

typedef struct istranscribe_whisper_context_v1 istranscribe_whisper_context_v1;

/*
 * Output view lifetime contract:
 * - probe and contextless error views must be copied before the next matching
 *   call on the same OS thread;
 * - context info, context error, and segment views must be copied before the
 *   next mutating context call or context destruction.
 * The only operation permitted concurrently with transcription is cancel.
 * After cancel, the owner must join transcription before destroying the
 * context, and the context is terminal for further transcription calls.
 */

typedef void (ISTRANSCRIBE_WHISPER_CALL * istranscribe_whisper_progress_callback_v1)(
    void * user_data,
    uint32_t percent);

ISTRANSCRIBE_WHISPER_API uint32_t ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_abi_version_v1(void);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_probe_backend_v1(
    istranscribe_whisper_backend_v1 requested_backend,
    istranscribe_whisper_backend_probe_v1 * probe);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_create_v1(
    const istranscribe_whisper_context_options_v1 * options,
    istranscribe_whisper_context_v1 ** context);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_transcribe_v1(
    istranscribe_whisper_context_v1 * context,
    const istranscribe_whisper_transcribe_options_v1 * options,
    istranscribe_whisper_progress_callback_v1 progress_callback,
    void * progress_user_data);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_cancel_v1(istranscribe_whisper_context_v1 * context);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_segment_count_v1(
    const istranscribe_whisper_context_v1 * context,
    uint32_t * segment_count);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_segment_get_v1(
    const istranscribe_whisper_context_v1 * context,
    uint32_t segment_index,
    istranscribe_whisper_segment_v1 * segment);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_timings_v1(
    const istranscribe_whisper_context_v1 * context,
    istranscribe_whisper_timings_v1 * timings);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_get_info_v1(
    const istranscribe_whisper_context_v1 * context,
    istranscribe_whisper_context_info_v1 * info);

ISTRANSCRIBE_WHISPER_API istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_last_error_v1(
    const istranscribe_whisper_context_v1 * context,
    istranscribe_whisper_error_v1 * error);

ISTRANSCRIBE_WHISPER_API void ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_destroy_v1(istranscribe_whisper_context_v1 * context);

#ifdef __cplusplus
}
#endif

#endif
