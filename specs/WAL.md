# WAL

> Checkpoints создаются только для незавершённой работы, которую нужно продолжить или передать.

## Active Checkpoints

### WI-017: Public GitHub open-source repository (@nikita)
- Work: [WI-017](work/WI-017-public-github-repository.md)
- Updated: 2026-09-26
- Checkpoint: MIT, English/Russian README, build/privacy/third-party docs, issue forms and security reporting prepared. Publication is authorized; the public repository will start from a clean snapshot, with earlier history retained privately.
- Next: Publish the clean snapshot, verify anonymous access and close WI-017.
- Blocker: —

### WI-014: Zoom screen-share recording continuity (@nikita)
- Work: [WI-014](work/WI-014-zoom-screen-share-continuity.md)
- Updated: 2026-08-19
- Checkpoint: Active-session continuity, candidate suppression и macOS Zoom meeting-host fallback для смены Space реализованы; объединённый macOS solution filter 606/606, Release build и format проходят. Проверенная app-кандидатура установлена в `/Applications/isTranscribe.app` без запуска живой встречи.
- Next: пользовательский прогон Record → смена macOS Space или share >20 s → возврат → leave в Zoom.
- Blocker: User-run live Zoom transition.

### WI-005: Online Groq/OpenRouter speaker-aware adaptation (@nikita)
- Work: [WI-005](work/WI-005-cloud-transcription.md)
- Updated: 2026-09-25
- Checkpoint: Groq and OpenRouter turbo each pass real EN/PT and EN/RU speaker-aware queue runs on macOS; self + two remote identities, primary/source cleanup and usage provenance verified. Fixed provider language normalization and all-endpoint ZDR preflight. Common macOS suite 649/649. Evidence: artifacts/acceptance/FEAT-017/macos-arm64/online-verification.md.
- Next: Finish live retry/cancel, provider setup UI and remaining privacy/billing evidence. Windows source changes have been reconciled.
- Blocker: Provider-account and manual UI acceptance remain open.

### WI-006: Full large-v3-turbo local adaptation (@nikita)
- Work: [WI-006](work/WI-006-local-whisper-transcription.md)
- Updated: 2026-09-25
- Checkpoint: Full turbo catalog/migration and multilingual timestamps implemented. Windows full-model Vulkan, packaged worker, speaker runtime and durable queue passed; CPU passed with an existing base model. Windows Release/MSIX preparation passed. macOS packaged worker and OS-network-denied real queue also passed in earlier work.
- Next: Finish full-model CPU, model install/resource/cancellation, Windows OS-level offline and visual acceptance.
- Blocker: Clean-platform resource/offline matrix and manual UI acceptance remain open.

### WI-016: Shared speaker-aware transcription pipeline (@nikita)
- Work: [WI-016](work/WI-016-speaker-aware-transcription.md)
- Updated: 2026-09-25
- Checkpoint: Shared pipeline and source cleanup passed real local and Groq/OpenRouter queues on macOS. Windows native speaker/Whisper, durable local queue and development MSIX preparation now pass. A prior two-chunk diagnostic retained self and two remote speakers, with uncertain words labelled speaker_unresolved.
- Next: Continue representative multi-chunk/overlap quality and platform capture/visual acceptance after manual review of the prepared Windows build.
- Blocker: Revised real multi-chunk acceptance and user visual review remain open. Source publication does not close these gates.

### WI-002: Windows release evidence (@nikita)
- Work: [WI-002](work/WI-002-windows-release-evidence.md)
- Updated: 2026-07-31
- Checkpoint: Ask → record подтверждён; 20-second automatic finish реализован, targeted regression, format и Desktop Release build проходили. Свежая Release-версия запускалась.
- Next: подтвердить automatic finish и готовый MP3 на реальной Zoom-встрече; затем собрать remaining live acceptance evidence.
- Blocker: Real Zoom observation, attended UAC and remaining live acceptance.

## Cross-work

- macOS parity выполняется цепочкой `WI-004 → WI-007 → WI-008 → WI-009 → WI-010 → WI-011 → WI-012`; `WI-005` и `WI-006` используют `WI-016` shared pipeline и зависят от завершённого `WI-012` для финальной macOS acceptance.
- `WI-003` использует MSIX payload/acceptance `INFRA-005.A` и third-party notice evidence `INFRA-005.C`.
- `WI-014` использует общий active-session continuity contract; platform-specific evidence подтверждается отдельно на macOS и Windows.
- `WI-016` owns shared modes, source retention, local diarization and speaker
  artifacts; `WI-005` and `WI-006` adapt online and local ASR engines to its
  required word-timestamp contract.

## Decisions Pending

- `WI-003`: Partner Center identity, hosted reviewed privacy/support URLs, age rating и private-flight readiness требуют внешнего решения или доступа.
