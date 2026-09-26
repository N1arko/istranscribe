-- @spec spec://modules/app/FEAT-014-transcription-extension-seam#deactivation
-- @spec spec://modules/app/FEAT-014-transcription-extension-seam#legacy-artifacts
UPDATE meeting_session
SET transcription_status = CASE
    WHEN transcription_status IN ('queued', 'uploading', 'processing', 'retry_scheduled')
        THEN 'legacy_inactive'
    WHEN transcription_status = 'failed'
        THEN 'legacy_failed'
    ELSE transcription_status
END
WHERE transcription_status IN (
    'queued',
    'uploading',
    'processing',
    'retry_scheduled',
    'failed');
