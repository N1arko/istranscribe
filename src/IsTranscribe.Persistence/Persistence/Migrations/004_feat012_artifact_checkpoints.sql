-- @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#migration
-- @spec spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization
ALTER TABLE meeting_session ADD COLUMN primary_audio_path TEXT;
ALTER TABLE meeting_session ADD COLUMN temp_session_path TEXT;
ALTER TABLE meeting_session ADD COLUMN source_manifest_path TEXT;
ALTER TABLE meeting_session ADD COLUMN staged_primary_path TEXT;
ALTER TABLE meeting_session ADD COLUMN artifact_progress REAL NOT NULL DEFAULT 0;
ALTER TABLE meeting_session ADD COLUMN source_cleanup_pending INTEGER NOT NULL DEFAULT 0;
ALTER TABLE meeting_session ADD COLUMN artifact_error_code TEXT;
ALTER TABLE meeting_session ADD COLUMN artifact_error_message TEXT;

UPDATE meeting_session
SET primary_audio_path = COALESCE(
    NULLIF(TRIM(audio_mix_path), ''),
    NULLIF(TRIM(audio_output_path), ''),
    NULLIF(TRIM(audio_mic_path), ''))
WHERE primary_audio_path IS NULL;
