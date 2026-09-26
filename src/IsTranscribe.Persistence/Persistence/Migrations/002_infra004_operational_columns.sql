ALTER TABLE meeting_session ADD COLUMN queued_at TEXT;
ALTER TABLE meeting_session ADD COLUMN updated_at TEXT;
ALTER TABLE meeting_session ADD COLUMN retry_attempt_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE meeting_session ADD COLUMN next_retry_at TEXT;
ALTER TABLE meeting_session ADD COLUMN last_retry_at TEXT;
