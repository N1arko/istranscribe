---
status: superseded
---

# PROP-002: App Shell And Settings Canon {#root}

## Простыми словами {#plain-language}

Этот документ описывает пользовательскую оболочку продукта: какие окна и control surfaces существуют в MVP, какие настройки пользователь обязан и может менять, и как tray-приложение остаётся управляемым без постоянного открытого окна.

## Goal {#goal}

Зафиксировать долгоживущий канон UI shell, settings catalog и user control surfaces.

## Superseded by {#superseded-by}

- Release v2 shell and settings are governed by `spec://modules/app/FEAT-013-minimal-desktop-experience#root` and `spec://common/PROP-006-release-v2-product-canon#experience-canon`.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#rules`

## Scope {#scope}

### In scope {#scope.in}
- first-run setup wizard;
- settings sections и их обязательные поля;
- main window surfaces;
- tray menu, hotkeys и ask-confirmation UX.

### Out of scope {#scope.out}
- low-level audio adapters;
- SQLite schema and secret storage implementation;
- Fireworks transport internals.

## Settings {#settings}

Этот historical MVP settings canon сохранён для legacy ownership; release-v2 settings принадлежат FEAT-013.

## Rules {#rules}

- MVP shell обязан поддерживать четыре основные поверхности: `Home`, `Current Recording`, `Settings`, `First-run setup wizard`.
- Tray menu обязан содержать `Start Force Record`, `Stop current recording`, `Privacy Pause on/off`, `Open app`, `Quit`.
- `Ask` confirmation показывается как заметный, но ненавязчивый toast/dialog поверх других окон и по таймауту по умолчанию считается ответом `No`.
- Таймаут ask-confirmation по умолчанию: `8 секунд`.
- UI обязан явно показывать активный `Privacy Pause`.
- MVP settings делятся на разделы `General`, `Recording`, `Devices`, `Applications`, `Storage`, `Transcription`.
- `General` включает минимум: автозапуск с Windows, сворачивание в tray при закрытии, уведомления, язык интерфейса (`ru`/`en`), настраиваемые hotkeys.
- `Recording` включает минимум: режим `Off/Ask/Auto`, prebuffer duration, silence threshold, start delay, stop delay, merge-nearby segments, privacy-pause policy, default sources for `Auto` and `Force Record`.
- `Devices` включает минимум: выбор output/microphone device, следование system default, auto-discover output devices, auto-discover microphone, policy для смены output, policy для смены/пропажи microphone, policy во время active recording.
- `Applications` включает минимум: whitelist list, add from running processes, manual `.exe`, enable/disable, ordering, auto-discovery toggle, policy for new apps, exclusions.
- `Storage` включает минимум: recordings folder, transcripts folder, failed/temp folder, filename template, keep raw after successful transcription, temp retention period.
- `Transcription` включает минимум: Fireworks API key, model, diarization toggle, min/max speakers, language auto/fixed, retry toggle, retry count.
- `Current Recording` surface показывает recording status, source, timer, microphone indicator, output indicator и действия `Pause`, `Stop`, `Discard`.
- `Home` показывает последние meeting sessions, их статусы, source app, start/end time и действия открытия папки, открытия `.md` и retry.
- Hotkeys в MVP поддерживают минимум: `Start/Stop Force Record`, `Privacy Pause on/off`, `Discard current recording`, `Open main window`.

## Acceptance {#acceptance}

Канон корректен, если:
- пользователь может полностью управлять MVP через shell surface без ручной правки конфигов;
- settings catalog зафиксирован явно, а не через неформальную ссылку на старое ТЗ;
- обязательные tray/hotkey/ask flows не могут быть silently removed.

## Document Notes {#document-notes}

- 2026-04-02: Extracted and consolidated from the original meeting-capture TZ.
- 2026-07-11: Linked the compact release v2 experience canon.
