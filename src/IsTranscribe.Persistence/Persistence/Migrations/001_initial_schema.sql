CREATE TABLE meeting_session (
    id                    TEXT    PRIMARY KEY,
    created_at            TEXT    NOT NULL,
    started_at            TEXT,
    ended_at              TEXT,
    status                TEXT    NOT NULL,
    mode                  TEXT    NOT NULL,
    source_type           TEXT    NOT NULL,
    source_app            TEXT,
    source_process_id     INTEGER,
    output_device_id      TEXT,
    microphone_device_id  TEXT,
    audio_output_path     TEXT,
    audio_mic_path        TEXT,
    audio_mix_path        TEXT,
    transcript_md_path    TEXT,
    transcript_json_path  TEXT,
    duration_seconds      REAL,
    transcription_status  TEXT    NOT NULL DEFAULT 'not_started',
    transcription_model   TEXT,
    diarization_enabled   INTEGER NOT NULL DEFAULT 1,
    language              TEXT,
    error_code            TEXT,
    error_message         TEXT,
    user_discarded        INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX idx_meeting_session_status ON meeting_session(status);
CREATE INDEX idx_meeting_session_created_at ON meeting_session(created_at);
CREATE INDEX idx_meeting_session_transcription_status ON meeting_session(transcription_status);

CREATE TABLE app_rule (
    id                    TEXT    PRIMARY KEY,
    display_name          TEXT    NOT NULL,
    process_name          TEXT    NOT NULL,
    enabled               INTEGER NOT NULL DEFAULT 1,
    capture_mode_default  TEXT,
    notes                 TEXT
);

CREATE UNIQUE INDEX idx_app_rule_process_name ON app_rule(process_name);
