-- @spec spec://modules/app/FEAT-016-local-whisper-transcription#inference
-- @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
-- @spec spec://modules/app/FEAT-016-local-whisper-transcription#worker
ALTER TABLE transcription_job
    ADD COLUMN trigger_kind TEXT NOT NULL DEFAULT 'manual'
    CHECK (trigger_kind IN ('manual', 'automatic'));

ALTER TABLE transcription_job
    ADD COLUMN supersedes_job_id TEXT
    REFERENCES transcription_job(id) ON DELETE SET NULL
    CHECK (supersedes_job_id IS NULL OR supersedes_job_id <> id);

CREATE INDEX idx_transcription_job_supersedes
    ON transcription_job(supersedes_job_id);

CREATE TABLE transcription_local_job (
    job_id                         TEXT    PRIMARY KEY,
    model_catalog_version          INTEGER NOT NULL,
    model_catalog_revision         TEXT    NOT NULL,
    model_format                   TEXT    NOT NULL,
    model_path                     TEXT    NOT NULL,
    model_size_bytes               INTEGER NOT NULL,
    model_sha256                   TEXT    NOT NULL,
    runtime_version                TEXT    NOT NULL,
    runtime_commit                 TEXT    NOT NULL,
    runtime_source_archive_sha256  TEXT    NOT NULL,
    native_bundle_manifest_sha256  TEXT    NOT NULL,
    bridge_abi_version             INTEGER NOT NULL,
    worker_protocol_version        INTEGER NOT NULL,
    requested_backend              TEXT    NOT NULL,
    resolved_backend               TEXT,
    backend_history_json           TEXT,
    thread_count                   INTEGER NOT NULL,
    inference_parameters_json      TEXT    NOT NULL,
    chunk_profile_version          INTEGER NOT NULL,
    run_identity_sha256            TEXT    NOT NULL,
    policy_defer_reason            TEXT,
    native_crash_count             INTEGER NOT NULL DEFAULT 0,
    last_crash_backend             TEXT,
    processing_duration_milliseconds INTEGER,
    peak_working_set_bytes         INTEGER,
    created_at                     TEXT    NOT NULL,
    updated_at                     TEXT    NOT NULL,
    FOREIGN KEY(job_id) REFERENCES transcription_job(id) ON DELETE CASCADE,
    CHECK (model_catalog_version > 0),
    CHECK (length(trim(model_catalog_revision)) > 0),
    CHECK (length(trim(model_format)) > 0),
    CHECK (length(trim(model_path)) > 0),
    CHECK (model_size_bytes > 0),
    CHECK (length(model_sha256) = 64 AND lower(model_sha256) NOT GLOB '*[^0-9a-f]*'),
    CHECK (length(trim(runtime_version)) > 0),
    CHECK (length(trim(runtime_commit)) > 0),
    CHECK (
        length(runtime_source_archive_sha256) = 64
        AND lower(runtime_source_archive_sha256) NOT GLOB '*[^0-9a-f]*'),
    CHECK (
        length(native_bundle_manifest_sha256) = 64
        AND lower(native_bundle_manifest_sha256) NOT GLOB '*[^0-9a-f]*'),
    CHECK (bridge_abi_version > 0),
    CHECK (worker_protocol_version > 0),
    CHECK (requested_backend IN ('auto', 'cpu', 'metal', 'vulkan')),
    CHECK (resolved_backend IS NULL OR resolved_backend IN ('cpu', 'metal', 'vulkan')),
    CHECK (backend_history_json IS NULL OR json_valid(backend_history_json)),
    CHECK (thread_count > 0 AND thread_count <= 64),
    CHECK (json_valid(inference_parameters_json)),
    CHECK (chunk_profile_version > 0),
    CHECK (length(run_identity_sha256) = 64 AND lower(run_identity_sha256) NOT GLOB '*[^0-9a-f]*'),
    CHECK (policy_defer_reason IS NULL OR length(trim(policy_defer_reason)) > 0),
    CHECK (native_crash_count >= 0),
    CHECK (last_crash_backend IS NULL OR last_crash_backend IN ('cpu', 'metal', 'vulkan')),
    CHECK (processing_duration_milliseconds IS NULL OR processing_duration_milliseconds >= 0),
    CHECK (peak_working_set_bytes IS NULL OR peak_working_set_bytes >= 0)
);

CREATE INDEX idx_transcription_local_job_model
    ON transcription_local_job(model_sha256, runtime_source_archive_sha256);

CREATE INDEX idx_transcription_local_job_policy_defer
    ON transcription_local_job(policy_defer_reason, updated_at);

CREATE TABLE transcription_local_chunk_attempt (
    job_id                         TEXT    NOT NULL,
    chunk_id                       TEXT    NOT NULL,
    attempt_index                  INTEGER NOT NULL,
    requested_backend              TEXT    NOT NULL,
    resolved_backend               TEXT,
    worker_started_at              TEXT    NOT NULL,
    worker_ended_at                TEXT,
    exit_category                  TEXT,
    decode_duration_milliseconds   INTEGER,
    inference_duration_milliseconds INTEGER,
    peak_working_set_bytes         INTEGER,
    stable_failure_category        TEXT,
    created_at                     TEXT    NOT NULL,
    PRIMARY KEY(job_id, chunk_id, attempt_index),
    FOREIGN KEY(job_id) REFERENCES transcription_local_job(job_id) ON DELETE CASCADE,
    FOREIGN KEY(job_id, chunk_id)
        REFERENCES transcription_chunk(job_id, id) ON DELETE CASCADE,
    CHECK (attempt_index >= 0),
    CHECK (requested_backend IN ('auto', 'cpu', 'metal', 'vulkan')),
    CHECK (resolved_backend IS NULL OR resolved_backend IN ('cpu', 'metal', 'vulkan')),
    CHECK (exit_category IS NULL OR exit_category IN (
        'completed',
        'cancelled',
        'preempted',
        'crashed',
        'out_of_memory',
        'timeout',
        'decode_failure',
        'native_failure',
        'protocol_failure')),
    CHECK (decode_duration_milliseconds IS NULL OR decode_duration_milliseconds >= 0),
    CHECK (inference_duration_milliseconds IS NULL OR inference_duration_milliseconds >= 0),
    CHECK (peak_working_set_bytes IS NULL OR peak_working_set_bytes >= 0),
    CHECK (stable_failure_category IS NULL OR length(trim(stable_failure_category)) > 0)
);

CREATE INDEX idx_transcription_local_chunk_attempt_started
    ON transcription_local_chunk_attempt(job_id, worker_started_at, chunk_id, attempt_index);
