// App-owned, pinned-source extension appended to whisper.cpp during the
// verified build. It exposes only the actual primary backend selected by the
// private whisper_state; the symbol stays internal to the final shared library.
// @spec spec://modules/app/FEAT-016-local-whisper-transcription#acceleration
extern "C" int32_t istranscribe_whisper_internal_has_gpu_backend_v1(
    const whisper_context * context) {
    if (context == nullptr || context->state == nullptr || context->state->backends.empty()) {
        return -1;
    }

    for (ggml_backend_t backend : context->state->backends) {
        ggml_backend_dev_t device = backend == nullptr ? nullptr : ggml_backend_get_device(backend);
        if (device == nullptr) {
            return -1;
        }

        const enum ggml_backend_dev_type type = ggml_backend_dev_type(device);
        if (type == GGML_BACKEND_DEVICE_TYPE_GPU || type == GGML_BACKEND_DEVICE_TYPE_IGPU) {
            return 1;
        }
    }

    return 0;
}

extern "C" const char * istranscribe_whisper_internal_primary_backend_name_v1(
    const whisper_context * context) {
    if (context == nullptr || context->state == nullptr || context->state->backends.empty()) {
        return nullptr;
    }

    ggml_backend_t backend = context->state->backends.front();
    ggml_backend_dev_t device = backend == nullptr ? nullptr : ggml_backend_get_device(backend);
    return device == nullptr ? nullptr : ggml_backend_dev_name(device);
}
