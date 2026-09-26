# Data and privacy

isTranscribe records microphone and system audio after the user confirms a meeting prompt or starts recording manually. The app keeps the resulting audio in the selected recordings folder.

Settings, the local database, transcripts, model files, and diagnostic logs are stored on the computer. On Windows, the application data root is `%LOCALAPPDATA%\isTranscribe`. Recordings may be in a different folder chosen by the user. API keys use the platform's protected secret storage.

## Transcription modes

| Mode | Audio processing | Network use |
| --- | --- | --- |
| Off | Audio is saved without a transcription job. | No transcription upload. |
| Local | Whisper and the speaker worker run on the computer. | Models and runtime assets may need to be downloaded. Inference uses installed files. |
| Online | Audio is sent to the selected Groq or OpenRouter service after opt-in. | Provider requests, capability checks, and audio uploads use the user's provider key. Provider terms and charges apply. |

The app keeps the primary recording when a transcription fails. Temporary source tracks and processing checkpoints support recovery; completed jobs clean up source tracks according to the storage policy.

## Sharing a bug report

Logs can contain local file paths and diagnostic details. Review them before attaching them to a public issue. A short description of the problem and a synthetic sample are often enough; private recordings and provider keys should stay out of the report.

Current implementation and open acceptance work are recorded in the [work board](../specs/BOARD.md). This document describes the application's data flow and does not replace a transcription provider's privacy policy.
