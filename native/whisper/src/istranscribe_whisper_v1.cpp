#include "istranscribe_whisper_v1.h"

#include "ggml-backend.h"
#include "whisper.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <iterator>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <utility>
#include <vector>

extern "C" int32_t istranscribe_whisper_internal_has_gpu_backend_v1(
    const whisper_context * context);
extern "C" const char * istranscribe_whisper_internal_primary_backend_name_v1(
    const whisper_context * context);

#ifndef ISTRANSCRIBE_WHISPER_ACCELERATOR
#define ISTRANSCRIBE_WHISPER_ACCELERATOR 0
#endif

namespace {

struct error_state {
    istranscribe_whisper_result_v1 code = ISTRANSCRIBE_WHISPER_OK_V1;
    std::string message;
};

struct segment_state {
    int64_t start_ms;
    int64_t end_ms;
    std::string text;
};

thread_local error_state thread_error;
thread_local std::string probe_device_name;
thread_local std::string probe_runtime_version;
std::once_flag log_initialization;

void discard_upstream_log(enum ggml_log_level, const char *, void *) {
}

void initialize_upstream_logging() {
    std::call_once(log_initialization, []() {
        // Worker diagnostics own structured messages. Upstream output is
        // intentionally discarded so transcript text cannot enter stderr.
        // @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
        whisper_log_set(discard_upstream_log, nullptr);
    });
}

bool has_size(uint32_t actual, std::size_t required) {
    return static_cast<uint64_t>(actual) >= static_cast<uint64_t>(required);
}

bool is_valid_utf8(const uint8_t * data, uint64_t length) {
    uint64_t index = 0;
    while (index < length) {
        const uint8_t first = data[index++];
        if (first <= 0x7f) {
            if (first == 0) {
                return false;
            }
            continue;
        }

        uint32_t code_point = 0;
        uint32_t continuation_count = 0;
        if ((first & 0xe0) == 0xc0) {
            code_point = first & 0x1f;
            continuation_count = 1;
            if (code_point < 2) {
                return false;
            }
        } else if ((first & 0xf0) == 0xe0) {
            code_point = first & 0x0f;
            continuation_count = 2;
        } else if ((first & 0xf8) == 0xf0) {
            code_point = first & 0x07;
            continuation_count = 3;
        } else {
            return false;
        }

        if (length - index < continuation_count) {
            return false;
        }

        for (uint32_t offset = 0; offset < continuation_count; ++offset) {
            const uint8_t continuation = data[index++];
            if ((continuation & 0xc0) != 0x80) {
                return false;
            }
            code_point = (code_point << 6) | (continuation & 0x3f);
        }

        if ((continuation_count == 2 && code_point < 0x800) ||
            (continuation_count == 3 && code_point < 0x10000) ||
            (code_point >= 0xd800 && code_point <= 0xdfff) ||
            code_point > 0x10ffff) {
            return false;
        }
    }

    return true;
}

bool validate_view(const istranscribe_whisper_utf8_view_v1 & view) {
    if (!has_size(view.struct_size, sizeof(istranscribe_whisper_utf8_view_v1)) ||
        view.reserved != 0 ||
        view.length > static_cast<uint64_t>(std::numeric_limits<std::size_t>::max())) {
        return false;
    }

    if (view.length == 0) {
        return view.data == nullptr;
    }

    return view.data != nullptr && is_valid_utf8(view.data, view.length);
}

template<std::size_t Size>
bool reserved_is_zero(const uint64_t (&values)[Size]) {
    return std::all_of(std::begin(values), std::end(values), [](uint64_t value) {
        return value == 0;
    });
}

std::string copy_view(const istranscribe_whisper_utf8_view_v1 & view) {
    return std::string(
        reinterpret_cast<const char *>(view.data),
        static_cast<std::size_t>(view.length));
}

istranscribe_whisper_utf8_view_v1 make_view(const std::string & value) {
    return istranscribe_whisper_utf8_view_v1{
        static_cast<uint32_t>(sizeof(istranscribe_whisper_utf8_view_v1)),
        0,
        value.empty() ? nullptr : reinterpret_cast<const uint8_t *>(value.data()),
        static_cast<uint64_t>(value.size())};
}

bool backend_is_compiled(istranscribe_whisper_backend_v1 backend) {
    if (backend == ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1) {
        return true;
    }

    return backend == static_cast<istranscribe_whisper_backend_v1>(
        ISTRANSCRIBE_WHISPER_ACCELERATOR);
}

bool device_matches(
    enum ggml_backend_dev_type type,
    istranscribe_whisper_backend_v1 backend) {
    if (backend == ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1) {
        return type == GGML_BACKEND_DEVICE_TYPE_CPU;
    }

    return type == GGML_BACKEND_DEVICE_TYPE_GPU ||
        type == GGML_BACKEND_DEVICE_TYPE_IGPU;
}

bool resolved_backend_matches(
    istranscribe_whisper_backend_v1 requested_backend,
    int32_t has_gpu_backend) {
    if (requested_backend == ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1) {
        return has_gpu_backend == 0;
    }

    return has_gpu_backend == 1;
}

bool copy_bounded_utf8(const char * value, std::size_t maximum_bytes, std::string & destination) {
    destination.clear();
    if (value == nullptr) {
        return false;
    }

    const std::size_t length = std::strlen(value);
    if (length == 0 || length > maximum_bytes || !is_valid_utf8(
        reinterpret_cast<const uint8_t *>(value),
        static_cast<uint64_t>(length))) {
        return false;
    }

    destination.assign(value, length);
    return true;
}

void set_thread_error(istranscribe_whisper_result_v1 code, std::string message) {
    thread_error.code = code;
    thread_error.message = std::move(message);
}

void clear_thread_error() {
    set_thread_error(ISTRANSCRIBE_WHISPER_OK_V1, {});
}

bool valid_language(const std::string & language) {
    return language.empty() || language == "auto" || whisper_lang_id(language.c_str()) >= 0;
}

struct progress_state {
    std::atomic<bool> * cancelled;
    istranscribe_whisper_progress_callback_v1 callback;
    void * callback_user_data;
};

void upstream_progress_callback(
    struct whisper_context *,
    struct whisper_state *,
    int progress,
    void * user_data) {
    auto * state = static_cast<progress_state *>(user_data);
    if (state == nullptr || state->callback == nullptr ||
        state->cancelled->load(std::memory_order_relaxed)) {
        return;
    }

    const auto bounded = static_cast<uint32_t>(std::clamp(progress, 0, 100));
    state->callback(state->callback_user_data, bounded);
}

bool upstream_encoder_begin_callback(
    struct whisper_context *,
    struct whisper_state *,
    void * user_data) {
    const auto * cancelled = static_cast<const std::atomic<bool> *>(user_data);
    return cancelled != nullptr && !cancelled->load(std::memory_order_relaxed);
}

bool upstream_abort_callback(void * user_data) {
    const auto * cancelled = static_cast<const std::atomic<bool> *>(user_data);
    return cancelled != nullptr && cancelled->load(std::memory_order_relaxed);
}

} // namespace

struct istranscribe_whisper_context_v1 {
    struct whisper_context * upstream = nullptr;
    istranscribe_whisper_backend_v1 backend = ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1;
    uint32_t thread_count = 1;
    std::atomic<bool> cancelled{false};
    mutable std::mutex operation_mutex;
    std::vector<segment_state> segments;
    istranscribe_whisper_timings_v1 timings{};
    std::string detected_language;
    std::string backend_device_name;
    error_state error;
};

namespace {

void set_context_error(
    istranscribe_whisper_context_v1 * context,
    istranscribe_whisper_result_v1 code,
    std::string message) {
    if (context == nullptr) {
        set_thread_error(code, std::move(message));
        return;
    }

    context->error.code = code;
    context->error.message = std::move(message);
}

void clear_context_error(istranscribe_whisper_context_v1 * context) {
    set_context_error(context, ISTRANSCRIBE_WHISPER_OK_V1, {});
}

} // namespace

uint32_t ISTRANSCRIBE_WHISPER_CALL istranscribe_whisper_abi_version_v1(void) {
    return ISTRANSCRIBE_WHISPER_ABI_VERSION_V1;
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_probe_backend_v1(
    istranscribe_whisper_backend_v1 requested_backend,
    istranscribe_whisper_backend_probe_v1 * probe) {
    try {
        if (probe == nullptr ||
            !has_size(probe->struct_size, sizeof(istranscribe_whisper_backend_probe_v1))) {
            set_thread_error(ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1, "Backend probe size is invalid.");
            return ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1;
        }

        initialize_upstream_logging();
        probe_device_name.clear();
        probe_runtime_version = whisper_version() == nullptr ? std::string{} : whisper_version();

        uint32_t available = 0;
        uint64_t memory_free = 0;
        uint64_t memory_total = 0;
        if (backend_is_compiled(requested_backend)) {
            const std::size_t device_count = ggml_backend_dev_count();
            for (std::size_t index = 0; index < device_count; ++index) {
                ggml_backend_dev_t device = ggml_backend_dev_get(index);
                if (device == nullptr || !device_matches(ggml_backend_dev_type(device), requested_backend)) {
                    continue;
                }

                ggml_backend_t initialized_backend = ggml_backend_dev_init(device, nullptr);
                if (initialized_backend == nullptr) {
                    continue;
                }

                std::size_t upstream_free = 0;
                std::size_t upstream_total = 0;
                ggml_backend_dev_memory(device, &upstream_free, &upstream_total);
                const char * name = ggml_backend_dev_name(device);
                if (!copy_bounded_utf8(name, 256, probe_device_name)) {
                    ggml_backend_free(initialized_backend);
                    continue;
                }
                memory_free = static_cast<uint64_t>(upstream_free);
                memory_total = static_cast<uint64_t>(upstream_total);
                available = 1;
                ggml_backend_free(initialized_backend);
                break;
            }
        }

        const uint32_t output_size = static_cast<uint32_t>(sizeof(*probe));
        *probe = {};
        probe->struct_size = output_size;
        probe->abi_version = ISTRANSCRIBE_WHISPER_ABI_VERSION_V1;
        probe->requested_backend = requested_backend;
        probe->available = available;
        probe->memory_free_bytes = memory_free;
        probe->memory_total_bytes = memory_total;
        probe->device_name = make_view(probe_device_name);
        probe->runtime_version = make_view(probe_runtime_version);

        if (!backend_is_compiled(requested_backend)) {
            set_thread_error(
                ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1,
                "The requested backend is absent from this runtime variant.");
            return ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1;
        }

        clear_thread_error();
        return ISTRANSCRIBE_WHISPER_OK_V1;
    } catch (...) {
        set_thread_error(ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1, "Backend probing failed.");
        return ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1;
    }
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_create_v1(
    const istranscribe_whisper_context_options_v1 * options,
    istranscribe_whisper_context_v1 ** context) {
    if (context != nullptr) {
        *context = nullptr;
    }

    try {
        if (options == nullptr || context == nullptr) {
            set_thread_error(ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Context options and output are required.");
            return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
        }
        if (!has_size(options->struct_size, sizeof(istranscribe_whisper_context_options_v1)) ||
            options->abi_version != ISTRANSCRIBE_WHISPER_ABI_VERSION_V1) {
            set_thread_error(ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1, "Context options use an unsupported ABI.");
            return ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1;
        }
        if (!validate_view(options->model_path) ||
            options->model_path.length == 0 ||
            options->thread_count == 0 || options->thread_count > 64 ||
            !reserved_is_zero(options->reserved)) {
            set_thread_error(ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Context options are invalid.");
            return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
        }

        istranscribe_whisper_backend_probe_v1 probe{};
        probe.struct_size = static_cast<uint32_t>(sizeof(probe));
        const auto probe_result = istranscribe_whisper_probe_backend_v1(options->backend, &probe);
        if (probe_result != ISTRANSCRIBE_WHISPER_OK_V1) {
            return probe_result;
        }
        if (probe.available == 0) {
            set_thread_error(
                ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1,
                "No compatible device is available for the requested backend.");
            return ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1;
        }

        const std::string model_path = copy_view(options->model_path);
        initialize_upstream_logging();

        whisper_context_params upstream_options = whisper_context_default_params();
        upstream_options.use_gpu = options->backend != ISTRANSCRIBE_WHISPER_BACKEND_CPU_V1;
        upstream_options.flash_attn = false;
        upstream_options.gpu_device = 0;
        upstream_options.dtw_token_timestamps = false;

        struct whisper_context * upstream = whisper_init_from_file_with_params(
            model_path.c_str(),
            upstream_options);
        if (upstream == nullptr) {
            set_thread_error(ISTRANSCRIBE_WHISPER_MODEL_LOAD_FAILED_V1, "The verified model could not be loaded.");
            return ISTRANSCRIBE_WHISPER_MODEL_LOAD_FAILED_V1;
        }

        const int32_t has_gpu_backend =
            istranscribe_whisper_internal_has_gpu_backend_v1(upstream);
        const char * resolved_device_name =
            istranscribe_whisper_internal_primary_backend_name_v1(upstream);
        std::string backend_device_name;
        if (!resolved_backend_matches(options->backend, has_gpu_backend) ||
            !copy_bounded_utf8(resolved_device_name, 256, backend_device_name)) {
            whisper_free(upstream);
            set_thread_error(
                ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1,
                "The requested backend did not become the primary inference backend.");
            return ISTRANSCRIBE_WHISPER_UNSUPPORTED_BACKEND_V1;
        }

        auto * created = new (std::nothrow) istranscribe_whisper_context_v1();
        if (created == nullptr) {
            whisper_free(upstream);
            set_thread_error(ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1, "Context allocation failed.");
            return ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1;
        }

        created->upstream = upstream;
        created->backend = options->backend;
        created->thread_count = options->thread_count;
        created->backend_device_name = std::move(backend_device_name);
        created->timings.struct_size = static_cast<uint32_t>(sizeof(created->timings));
        *context = created;
        clear_thread_error();
        return ISTRANSCRIBE_WHISPER_OK_V1;
    } catch (...) {
        set_thread_error(ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1, "Context creation failed.");
        return ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1;
    }
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_transcribe_v1(
    istranscribe_whisper_context_v1 * context,
    const istranscribe_whisper_transcribe_options_v1 * options,
    istranscribe_whisper_progress_callback_v1 progress_callback,
    void * progress_user_data) {
    if (context == nullptr) {
        set_thread_error(ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Context is required.");
        return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
    }

    std::lock_guard<std::mutex> lock(context->operation_mutex);
    try {
        if (options == nullptr) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Transcription options are required.");
            return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
        }
        if (!has_size(options->struct_size, sizeof(istranscribe_whisper_transcribe_options_v1)) ||
            options->abi_version != ISTRANSCRIBE_WHISPER_ABI_VERSION_V1) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1, "Transcription options use an unsupported ABI.");
            return ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1;
        }
        if (options->pcm_f32_16khz_mono == nullptr ||
            options->sample_count == 0 ||
            options->sample_count > ISTRANSCRIBE_WHISPER_MAX_SAMPLES_V1 ||
            !validate_view(options->language) ||
            !reserved_is_zero(options->reserved)) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Transcription options are invalid.");
            return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
        }
        for (uint64_t index = 0; index < options->sample_count; ++index) {
            if (!std::isfinite(options->pcm_f32_16khz_mono[index])) {
                set_context_error(context, ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "PCM samples must be finite.");
                return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
            }
        }

        const std::string language = copy_view(options->language);
        if (!valid_language(language)) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Language must be auto, ru, or en.");
            return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
        }

        // The bridge accepts one deterministic checkpoint of at most five
        // minutes. Chunk planning and persisted overlap live in Application.
        // @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
        if (context->cancelled.load(std::memory_order_relaxed)) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_CANCELLED_V1, "Transcription was cancelled.");
            return ISTRANSCRIBE_WHISPER_CANCELLED_V1;
        }
        context->segments.clear();
        context->detected_language.clear();
        context->timings = {};
        context->timings.struct_size = static_cast<uint32_t>(sizeof(context->timings));
        clear_context_error(context);
        whisper_reset_timings(context->upstream);

        progress_state progress{
            &context->cancelled,
            progress_callback,
            progress_user_data};

        whisper_full_params upstream_options = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
        upstream_options.n_threads = static_cast<int>(context->thread_count);
        upstream_options.translate = false;
        upstream_options.no_context = true;
        upstream_options.no_timestamps = false;
        upstream_options.single_segment = false;
        upstream_options.print_special = false;
        upstream_options.print_progress = false;
        upstream_options.print_realtime = false;
        upstream_options.print_timestamps = false;
        // @spec spec://modules/app/FEAT-017-speaker-aware-transcription#recognition
        // whisper.cpp's word splitting retains token-derived timing within the existing ABI.
        upstream_options.token_timestamps = true;
        upstream_options.max_len = 1;
        upstream_options.split_on_word = true;
        upstream_options.temperature = 0.0f;
        upstream_options.temperature_inc = 0.0f;
        upstream_options.language = language.empty() || language == "auto" ? nullptr : language.c_str();
        upstream_options.detect_language = false;
        upstream_options.progress_callback = upstream_progress_callback;
        upstream_options.progress_callback_user_data = &progress;
        upstream_options.encoder_begin_callback = upstream_encoder_begin_callback;
        upstream_options.encoder_begin_callback_user_data = &context->cancelled;
        upstream_options.abort_callback = upstream_abort_callback;
        upstream_options.abort_callback_user_data = &context->cancelled;

        const int upstream_result = whisper_full(
            context->upstream,
            upstream_options,
            options->pcm_f32_16khz_mono,
            static_cast<int>(options->sample_count));

        if (context->cancelled.load(std::memory_order_relaxed)) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_CANCELLED_V1, "Transcription was cancelled.");
            return ISTRANSCRIBE_WHISPER_CANCELLED_V1;
        }

        if (upstream_result != 0) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1, "Native inference failed.");
            return ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1;
        }

        const int segment_count = whisper_full_n_segments(context->upstream);
        if (segment_count < 0) {
            set_context_error(context, ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1, "Native segments are invalid.");
            return ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1;
        }

        context->segments.reserve(static_cast<std::size_t>(segment_count));
        int64_t previous_start_ms = 0;
        for (int index = 0; index < segment_count; ++index) {
            const char * text = whisper_full_get_segment_text(context->upstream, index);
            const int64_t start = whisper_full_get_segment_t0(context->upstream, index);
            const int64_t end = whisper_full_get_segment_t1(context->upstream, index);
            const std::size_t text_length = text == nullptr ? 0 : std::strlen(text);
            if (text_length > 0 && !is_valid_utf8(
                reinterpret_cast<const uint8_t *>(text),
                static_cast<uint64_t>(text_length))) {
                context->segments.clear();
                set_context_error(context, ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1, "Native segment text is not valid UTF-8.");
                return ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1;
            }
            const int64_t start_ms = std::max<int64_t>(0, start) * 10;
            const int64_t end_ms = std::max<int64_t>(0, end) * 10;
            if (start_ms < previous_start_ms || end_ms < start_ms) {
                context->segments.clear();
                set_context_error(context, ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1, "Native segment timestamps are invalid.");
                return ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1;
            }
            previous_start_ms = start_ms;
            context->segments.push_back(segment_state{
                start_ms,
                end_ms,
                text == nullptr ? std::string{} : std::string(text, text_length)});
        }

        const whisper_timings * timings = whisper_get_timings(context->upstream);
        if (timings != nullptr) {
            context->timings.sample_ms = timings->sample_ms;
            context->timings.encode_ms = timings->encode_ms;
            context->timings.decode_ms = timings->decode_ms;
            context->timings.batch_ms = timings->batchd_ms;
            context->timings.prompt_ms = timings->prompt_ms;
        }

        const int detected_language_id = whisper_full_lang_id(context->upstream);
        if (detected_language_id < 0 || !copy_bounded_utf8(
            whisper_lang_str(detected_language_id),
            35,
            context->detected_language)) {
            context->segments.clear();
            set_context_error(
                context,
                ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1,
                "Native language detection returned an invalid value.");
            return ISTRANSCRIBE_WHISPER_TRANSCRIPTION_FAILED_V1;
        }

        if (progress_callback != nullptr) {
            progress_callback(progress_user_data, 100);
        }
        return ISTRANSCRIBE_WHISPER_OK_V1;
    } catch (...) {
        set_context_error(context, ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1, "Transcription failed inside the native boundary.");
        return ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1;
    }
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_cancel_v1(istranscribe_whisper_context_v1 * context) {
    if (context == nullptr) {
        set_thread_error(ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Context is required.");
        return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
    }

    // This is the only operation intentionally callable while inference owns
    // the context mutex.
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
    context->cancelled.store(true, std::memory_order_relaxed);
    return ISTRANSCRIBE_WHISPER_OK_V1;
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_segment_count_v1(
    const istranscribe_whisper_context_v1 * context,
    uint32_t * segment_count) {
    if (context == nullptr || segment_count == nullptr) {
        set_thread_error(ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1, "Context and segment count are required.");
        return ISTRANSCRIBE_WHISPER_INVALID_ARGUMENT_V1;
    }

    std::lock_guard<std::mutex> lock(context->operation_mutex);
    if (context->segments.size() > std::numeric_limits<uint32_t>::max()) {
        set_context_error(
            const_cast<istranscribe_whisper_context_v1 *>(context),
            ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1,
            "Native segment count exceeds the ABI boundary.");
        return ISTRANSCRIBE_WHISPER_INTERNAL_ERROR_V1;
    }
    *segment_count = static_cast<uint32_t>(context->segments.size());
    clear_context_error(const_cast<istranscribe_whisper_context_v1 *>(context));
    return ISTRANSCRIBE_WHISPER_OK_V1;
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_segment_get_v1(
    const istranscribe_whisper_context_v1 * context,
    uint32_t segment_index,
    istranscribe_whisper_segment_v1 * segment) {
    if (context == nullptr || segment == nullptr ||
        !has_size(segment->struct_size, sizeof(istranscribe_whisper_segment_v1))) {
        set_thread_error(ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1, "Segment output size is invalid.");
        return ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1;
    }

    std::lock_guard<std::mutex> lock(context->operation_mutex);
    if (segment_index >= context->segments.size()) {
        set_context_error(
            const_cast<istranscribe_whisper_context_v1 *>(context),
            ISTRANSCRIBE_WHISPER_OUT_OF_RANGE_V1,
            "Native segment index is outside the completed result.");
        return ISTRANSCRIBE_WHISPER_OUT_OF_RANGE_V1;
    }

    const segment_state & source = context->segments[segment_index];
    *segment = {};
    segment->struct_size = static_cast<uint32_t>(sizeof(*segment));
    segment->start_ms = source.start_ms;
    segment->end_ms = source.end_ms;
    segment->text = make_view(source.text);
    clear_context_error(const_cast<istranscribe_whisper_context_v1 *>(context));
    return ISTRANSCRIBE_WHISPER_OK_V1;
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_timings_v1(
    const istranscribe_whisper_context_v1 * context,
    istranscribe_whisper_timings_v1 * timings) {
    if (context == nullptr || timings == nullptr ||
        !has_size(timings->struct_size, sizeof(istranscribe_whisper_timings_v1))) {
        set_thread_error(ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1, "Timings output size is invalid.");
        return ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1;
    }

    std::lock_guard<std::mutex> lock(context->operation_mutex);
    *timings = context->timings;
    clear_context_error(const_cast<istranscribe_whisper_context_v1 *>(context));
    return ISTRANSCRIBE_WHISPER_OK_V1;
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_get_info_v1(
    const istranscribe_whisper_context_v1 * context,
    istranscribe_whisper_context_info_v1 * info) {
    if (context == nullptr || info == nullptr ||
        !has_size(info->struct_size, sizeof(istranscribe_whisper_context_info_v1))) {
        set_thread_error(ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1, "Context info output size is invalid.");
        return ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1;
    }

    std::lock_guard<std::mutex> lock(context->operation_mutex);
    *info = {};
    info->struct_size = static_cast<uint32_t>(sizeof(*info));
    info->abi_version = ISTRANSCRIBE_WHISPER_ABI_VERSION_V1;
    info->resolved_backend = context->backend;
    info->detected_language = make_view(context->detected_language);
    info->backend_device_name = make_view(context->backend_device_name);
    clear_context_error(const_cast<istranscribe_whisper_context_v1 *>(context));
    return ISTRANSCRIBE_WHISPER_OK_V1;
}

istranscribe_whisper_result_v1 ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_last_error_v1(
    const istranscribe_whisper_context_v1 * context,
    istranscribe_whisper_error_v1 * error) {
    if (error == nullptr || !has_size(error->struct_size, sizeof(istranscribe_whisper_error_v1))) {
        set_thread_error(ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1, "Error output size is invalid.");
        return ISTRANSCRIBE_WHISPER_ABI_MISMATCH_V1;
    }

    if (context == nullptr) {
        *error = {};
        error->struct_size = static_cast<uint32_t>(sizeof(*error));
        error->code = thread_error.code;
        error->message = make_view(thread_error.message);
        return ISTRANSCRIBE_WHISPER_OK_V1;
    }

    std::lock_guard<std::mutex> lock(context->operation_mutex);
    *error = {};
    error->struct_size = static_cast<uint32_t>(sizeof(*error));
    error->code = context->error.code;
    error->message = make_view(context->error.message);
    return ISTRANSCRIBE_WHISPER_OK_V1;
}

void ISTRANSCRIBE_WHISPER_CALL
istranscribe_whisper_context_destroy_v1(istranscribe_whisper_context_v1 * context) {
    if (context == nullptr) {
        return;
    }

    try {
        std::unique_lock<std::mutex> lock(context->operation_mutex);
        if (context->upstream != nullptr) {
            whisper_free(context->upstream);
            context->upstream = nullptr;
        }
        lock.unlock();
        delete context;
    } catch (...) {
        // No exception may cross the C ABI. The worker treats the context as
        // terminal and exits immediately after destruction.
    }
}
