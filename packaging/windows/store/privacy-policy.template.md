<!--
TEMPLATE FOR REVIEW AND HOSTING.
Replace every angle-bracket placeholder, verify the statements against the release,
and obtain an appropriate legal/privacy review before publication.
@spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
@spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
-->

# Privacy Policy for isTranscribe

Effective date: <YYYY-MM-DD>

Publisher: <publisher name>

Contact: <support email or HTTPS support URL>

## What the app accesses

isTranscribe records audio only after the user starts a manual recording or confirms a meeting recording prompt. A recording may contain microphone audio and audio played by applications on the Windows device. Audio can contain personal information shared during a call.

The app reads Windows audio-session and process information to detect signs of a meeting and to select an audio source. This information is processed on the device.

## How data is used

Audio is used to create the recording requested by the user. The app saves recordings as local MP3 files. If the user starts cloud transcription or enables automatic transcription, the app sends bounded audio chunks to the selected Groq or OpenRouter service and stores the returned transcript locally.

## Storage and retention

Recordings and transcripts are stored in folders selected by the user. Settings, recovery information, and diagnostic logs are stored locally on the Windows device. Provider API keys are stored with Windows-protected secret storage and are not shown again after saving. Local files remain until the user deletes or moves them. A transcription provider may retain uploaded data under its own published policy.

## Network transfer and third parties

Recording and meeting detection run locally. Cloud transcription is off by default. Audio chunks are sent to Groq or OpenRouter only after the user starts transcription or separately enables automatic transcription and accepts the audio-transfer disclosure. The selected service may charge the user's provider account and processes data under its own policy. Model discovery contacts the selected service after the user saves a key and asks to refresh models. The app does not upload settings or diagnostic logs and does not include advertising or analytics telemetry. The publisher does not receive meeting audio through this feature.

## User controls

The user decides whether to start each prompted recording. Recording can be paused, stopped, or discarded from the app. For cloud transcription, the user chooses a provider, saves or deletes its API key, accepts the current disclosure, starts a job, cancels it, and controls a separate automatic-transcription setting. Microphone use and startup behavior can be changed in the app and in Windows settings.

## Changes to this policy

This policy will be updated before another material change to data handling. The effective date above identifies the current version.

## Contact

Questions and privacy requests: <support email or HTTPS support URL>
