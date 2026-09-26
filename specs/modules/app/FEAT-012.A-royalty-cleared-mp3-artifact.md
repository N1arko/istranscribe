---
status: active
---

# FEAT-012.A: Royalty-Cleared MP3 Artifact {#root}

## Простыми словами {#plain-language}

После встречи пользователь получает один компактный `.mp3`-файл, который открывается штатными средствами Windows. Приложение использует системный MP3 encoder, не добавляет отдельный codec binary и сохраняет прежний надёжный recovery-контур. Старые `.m4a`-записи остаются доступными.

## Goal {#goal}

Заменить Windows AAC-LC/`.m4a` primary artifact на системный MP3/`.mp3`, чтобы убрать AAC patent-pool blocker из публичного релиза и сохранить лёгкую поставку, обычное shell playback и loss-resistant finalization.

## Depends on {#depends-on}

- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#root`
- `spec://common/PROP-006-release-v2-product-canon#recording-artifact`
- `spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#root`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#windows-adapters`

## Supersedes {#supersedes}

Для новых Windows release-v2 recordings эта change-спека заменяет codec/container решения из:

- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#artifact-contract.primary`;
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#compression`;
- AAC-specific verification wording из `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#verification`.

Mixing, finalization transaction, recovery, storage ownership и остальные acceptance требования `FEAT-012` сохраняются.

## Scope {#scope}

### In scope {#scope.in}

- `.mp3` primary artifact для новых Windows x64 recordings;
- operating-system Media Foundation MP3 encoder через существующую NAudio boundary;
- фиксированный speech-first preset;
- MP3 readability/duration verification до atomic promotion;
- legacy `.m4a` listing/open/delete без re-encode;
- release manifest, dependency inventory, Store copy и verifier migration с AAC на MP3;
- удаление shipping AAC encoder path и AAC review parameter/gate;
- deterministic golden, recovery, long-recording и package verification.

### Out of scope {#scope.out}

- перекодирование существующих `.m4a`, `.ogg` или `.wav`;
- codec picker в пользовательских настройках;
- bundled `ffmpeg`, LAME или другой MP3 codec binary;
- Opus playback surface;
- macOS encoder implementation;
- юридическое заключение по юрисдикциям вне зафиксированной engineering evidence boundary.

## Canonical Decisions {#decisions}

### Format and preset {#decisions.format}

- Новый Windows primary artifact имеет расширение `.mp3` и содержит MPEG-1 Layer III audio.
- Канонический input остаётся `48 kHz`, `16-bit`, stereo PCM после прежнего resample/mono-to-stereo этапа.
- Канонический output bitrate — `128 kbps`; preset не показывается как обычная настройка.
- Output-only, microphone-only и output+microphone проходят один и тот же encoder/finalizer.
- Staged artifact использует имя `.<final-name>.partial` в том же каталоге для atomic promotion.

### Implementation boundary {#decisions.implementation}

- Encoder выбирается через `MFAudioFormat_MP3` и пишет `MFTranscodeContainerType_MP3` средствами Microsoft Media Foundation.
- NAudio остаётся только MIT-licensed managed wrapper над Windows API.
- Release payload не содержит LAME, `ffmpeg`, `libmp3lame`, отдельный MP3/AAC codec executable или native codec library.
- Availability probe fail-closed проверяет exact `48 kHz / stereo / 128 kbps` media type до начала finalization.
- Release composition не содержит активного `MFAudioFormat_AAC` encoder path.

### Distribution evidence {#decisions.distribution-evidence}

- Microsoft документирует встроенный Media Foundation MP3 encoder для desktop apps начиная с Windows 8 и MP3 playback в Media Player на Windows 10/11.
- Fraunhofer IIS фиксирует прекращение MP3 licensing program `2017-04-23`; их пояснение связывает это с истечением последнего core MP3 patent, входившего в программу.
- Эти источники являются engineering evidence для отказа от AAC contour. Они не подменяют индивидуальную юридическую консультацию для нестандартной модели распространения или отдельной юрисдикции.
- Public release manifest хранит точные codec/runtime facts и ссылки на первичные источники. Свободное поле «AAC review» удаляется.

### User compatibility {#decisions.compatibility}

- `Open recording` продолжает открывать primary artifact через обычный Windows shell.
- Clean Windows 10/11 playback не требует codec pack от пользователя.
- Existing database rows с `.m4a` сохраняются и открываются прежним generic file action.
- Deletion allowlist включает `.mp3` вместе с историческими `.m4a`, `.ogg` и `.wav`.
- Store listing и privacy copy называют MP3 только после runtime/package cutover.

## Finalization Contract {#finalization}

- Source selection, mixing, progress, cancellation и recovery checkpoints остаются из `FEAT-012`.
- Encoder создаёт новый partial file, flush/close завершает MP3 stream, затем readability probe подтверждает non-empty audio и duration.
- Duration tolerance учитывает MP3 frame/padding rounding и остаётся достаточно узким для обнаружения truncation.
- Failed or cancelled encode удаляет только incomplete staged file и сохраняет readable source artifacts.
- Atomic promotion и repository `ready` checkpoint происходят только после повторного чтения staged artifact.

## Release Contract {#release-contract}

- `Build-WindowsRelease.ps1` не принимает `AacLicensingReviewReference` и не читает `ISTRANSCRIBE_AAC_LICENSING_REVIEW_REFERENCE`.
- `release-manifest.json` содержит exact `audioCodec` evidence: format, codec, system implementation, preset, shipped codec binaries count и primary-source URLs.
- Development, Production и Store verifiers проверяют один codec contract; Store artifact остаётся certificate-free и unsigned до Microsoft certification.
- Dependency inventory фиксирует NAudio license и не содержит codec implementation package.
- Store metadata, certification notes и privacy policy не обещают `.m4a` после cutover.

## Verification {#verification}

- Encoder tests проверяют availability, exact preset, MP3 stream signature, readability, cancellation cleanup, existing partial refusal, invalid source и two-hour bounded-memory encode.
- Golden test декодирует готовый MP3 и доказывает присутствие output и microphone signals.
- Path resolver, coordinator, deletion, recovery, persistence и UI tests используют `.mp3` для новых sessions и сохраняют explicit legacy `.m4a` cases.
- PATH-empty current-host encode и self-contained win-x64 publish проходят без bundled codec binary.
- Release artifact verifier подтверждает manifest contract и запрещает AAC/third-party codec payload.
- Clean-machine playback остаётся обязательным фактическим gate `INFRA-008`.

## Acceptance {#acceptance}

FEAT-012.A завершена, когда:

1. Новая manual и Ask recording завершается одним readable `.mp3` primary artifact.
2. Exact `48 kHz / stereo / 128 kbps` system Media Foundation MP3 path проходит golden и two-hour tests.
3. Shipping release composition не содержит активного AAC encoder path или bundled codec binary.
4. Старые `.m4a` записи остаются list/open/delete compatible и не перекодируются.
5. Failure/restart/cancellation не теряют все читаемые copies audio.
6. Store listing/privacy/release manifest/verifiers описывают MP3 contour и больше не требуют AAC review reference.
7. Release, legacy, format, self-contained publish и Store fixture gates проходят.

## Document Notes {#document-notes}

- 2026-07-12: Change-spec создан после release audit: Via Licensing Alliance указывает, что license может требоваться developers of end-user AAC encoder/decoder products. Для лёгкого public Windows release выбран system MP3 contour: Microsoft документирует встроенный encoder/playback, Fraunhofer — завершение MP3 licensing program после истечения core program patents.
- 2026-07-12: Acceptance закрыт. PATH-empty encoder 8/8, двухчасовой 7,200-second encode завершён за 143 seconds с peak private-memory growth 8,015,872 bytes; release 566/566, legacy 110/110, format, unsigned Store fixture `2.0.0-rc.9002` и strict metadata/package verification прошли. Evidence: `artifacts/acceptance/FEAT-012.A/windows-x64/verification-summary.json`.

## External References {#external-references}

- [Microsoft: MP3 Audio Encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/mp3-audio-encoder)
- [Microsoft: Codecs in Media Player](https://support.microsoft.com/en-us/windows/codecs-in-media-player-d5c2cdcd-83a2-4805-abb0-c6888138e456)
- [Fraunhofer IIS: mp3](https://www.iis.fraunhofer.de/en/ff/amm/consumer-electronics/mp3.html)
- [Fraunhofer IIS: mp3 software, patents and licenses](https://www.audioblog.iis.fraunhofer.com/mp3-software-patents-licenses)
- [Via Licensing Alliance: AAC program](https://www.via-la.com/licensing-programs/aac/)
