-- @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#queue-retry
-- @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#artifacts
CREATE TABLE transcription_job (
    id                       TEXT    PRIMARY KEY,
    session_id               TEXT    NOT NULL,
    engine_id                TEXT    NOT NULL,
    execution_kind           TEXT    NOT NULL,
    model_id                 TEXT    NOT NULL,
    engine_options_json      TEXT,
    requested_language       TEXT,
    detected_language        TEXT,
    input_audio_path         TEXT    NOT NULL,
    input_sha256             TEXT    NOT NULL,
    input_size_bytes         INTEGER NOT NULL,
    input_duration_seconds   REAL,
    status                   TEXT    NOT NULL,
    progress                 REAL    NOT NULL DEFAULT 0,
    current_chunk_index      INTEGER,
    current_chunk_id         TEXT,
    queued_at                TEXT    NOT NULL,
    created_at               TEXT    NOT NULL,
    updated_at               TEXT    NOT NULL,
    attempt_count            INTEGER NOT NULL DEFAULT 0,
    next_attempt_at          TEXT,
    last_attempt_at          TEXT,
    stable_error_code        TEXT,
    error_message            TEXT,
    transcript_md_path       TEXT,
    transcript_json_path     TEXT,
    usage_json               TEXT,
    remote_consent_revision  TEXT,
    remote_consent_at        TEXT,
    privacy_policy_json      TEXT,
    manifest_version         INTEGER NOT NULL DEFAULT 1,
    manifest_path            TEXT,
    artifact_publication_state TEXT NOT NULL DEFAULT 'none',
    staged_transcript_md_path   TEXT,
    staged_transcript_json_path TEXT,
    staged_transcript_md_sha256 TEXT,
    staged_transcript_json_sha256 TEXT,
    replace_existing         INTEGER NOT NULL DEFAULT 0,
    cancellation_requested   INTEGER NOT NULL DEFAULT 0,
    completed_at             TEXT,
    cancelled_at             TEXT,
    FOREIGN KEY(session_id) REFERENCES meeting_session(id) ON DELETE CASCADE,
    FOREIGN KEY(id, current_chunk_id)
        REFERENCES transcription_chunk(job_id, id)
        DEFERRABLE INITIALLY DEFERRED,
    CHECK (execution_kind IN ('remote', 'local')),
    CHECK (
        (engine_id IN ('remote.groq', 'remote.openrouter') AND execution_kind = 'remote')
        OR (engine_id = 'local.whisper' AND execution_kind = 'local')
    ),
    CHECK (
        execution_kind <> 'remote'
        OR (
            remote_consent_revision IS NOT NULL
            AND length(trim(remote_consent_revision)) > 0
            AND remote_consent_at IS NOT NULL
            AND length(trim(remote_consent_at)) > 0
            AND julianday(remote_consent_at) IS NOT NULL
        )
    ),
    CHECK (input_size_bytes >= 0),
    CHECK (input_duration_seconds IS NULL OR input_duration_seconds >= 0),
    CHECK (progress >= 0 AND progress <= 1),
    CHECK (attempt_count >= 0),
    CHECK (replace_existing IN (0, 1)),
    CHECK (cancellation_requested IN (0, 1)),
    CHECK (artifact_publication_state IN ('none', 'staged', 'promoted'))
);

CREATE INDEX idx_transcription_job_queue
    ON transcription_job(status, next_attempt_at, queued_at, id);

CREATE INDEX idx_transcription_job_session
    ON transcription_job(session_id, created_at DESC, id DESC);

CREATE UNIQUE INDEX idx_transcription_job_active_session
    ON transcription_job(session_id)
    WHERE status IN (
        'preparing',
        'queued',
        'uploading',
        'processing',
        'finalizing',
        'retry_scheduled',
        'attention_required');

CREATE TABLE transcription_chunk (
    id                        TEXT    NOT NULL,
    job_id                    TEXT    NOT NULL,
    sequence_index            INTEGER NOT NULL,
    parent_chunk_id           TEXT,
    split_depth               INTEGER NOT NULL DEFAULT 0,
    start_milliseconds        INTEGER NOT NULL,
    end_milliseconds          INTEGER NOT NULL,
    overlap_milliseconds      INTEGER NOT NULL DEFAULT 0,
    artifact_path             TEXT,
    artifact_format           TEXT,
    artifact_sha256           TEXT,
    artifact_size_bytes       INTEGER,
    status                    TEXT    NOT NULL DEFAULT 'pending',
    attempt_count             INTEGER NOT NULL DEFAULT 0,
    created_at                TEXT    NOT NULL,
    updated_at                TEXT    NOT NULL,
    engine_request_id         TEXT,
    result_path               TEXT,
    result_sha256             TEXT,
    result_metadata_json      TEXT,
    usage_json                TEXT,
    stable_error_code         TEXT,
    error_message             TEXT,
    PRIMARY KEY(job_id, id),
    FOREIGN KEY(job_id) REFERENCES transcription_job(id) ON DELETE CASCADE,
    FOREIGN KEY(job_id, parent_chunk_id) REFERENCES transcription_chunk(job_id, id),
    UNIQUE(job_id, sequence_index),
    UNIQUE(job_id, start_milliseconds, end_milliseconds, split_depth),
    CHECK (sequence_index >= 0),
    CHECK (split_depth >= 0),
    CHECK (start_milliseconds >= 0),
    CHECK (end_milliseconds > start_milliseconds),
    CHECK (overlap_milliseconds >= 0),
    CHECK (overlap_milliseconds <= end_milliseconds - start_milliseconds),
    CHECK (artifact_size_bytes IS NULL OR artifact_size_bytes >= 0),
    CHECK (attempt_count >= 0)
);

CREATE INDEX idx_transcription_chunk_pending
    ON transcription_chunk(job_id, status, start_milliseconds, sequence_index);

CREATE INDEX idx_transcription_chunk_parent
    ON transcription_chunk(job_id, parent_chunk_id);

ALTER TABLE meeting_session
    ADD COLUMN current_transcription_job_id TEXT
    REFERENCES transcription_job(id) ON DELETE SET NULL;

CREATE INDEX idx_meeting_session_current_transcription_job
    ON meeting_session(current_transcription_job_id);
