---
status: superseded
---

# PROP-003: Audio Capture And Device Observation Canon {#root}

## Простыми словами {#plain-language}

Этот документ фиксирует канон захвата звука и наблюдения за Windows-источниками: какие аудиоисточники поддерживаются, как определяется вероятное начало встречи, как работает prebuffer и как приложение реагирует на смену устройств.

## Goal {#goal}

Сделать detection/capture/device behavior единым каноном проекта, а не размазанным между отдельными фичами.

## Superseded by {#superseded-by}

- Release v2 detection behavior is governed by `spec://modules/app/FEAT-011-meeting-detection-v2#root`; current capture and device primitives are governed by `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#root` and `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#root`.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#rules`

## Scope {#scope}

### In scope {#scope.in}
- supported audio sources;
- detection signals for Auto and Ask;
- defaults for capture modes and prebuffer;
- device observation and switching behavior.

### Out of scope {#scope.out}
- UI layout of settings screens;
- transcription formatting;
- logging persistence mechanics.

## Rules {#rules}

- Поддерживаемые аудиоисточники MVP: `process output capture`, `device loopback capture`, `microphone capture`.
- В режиме `Auto` default capture source: `process output + microphone`.
- В режиме `Force Record` default capture source: `selected output device + microphone`.
- Пользователь может выбрать `output only`, `microphone only` или `both`.
- Output track, microphone track и optional mixed artifact считаются разными артефактами и не должны сливаться в неразличимый blob.
- Detection использует комбинацию сигналов: whitelisted process exists, audio session state is `Active`, signal level is above threshold for configured time.
- Для browser scenarios whitelisted process match считается valid even when actual audio comes from its process tree.
- Default minimum signal duration before auto start: `2 секунды`.
- Системные single-shot sounds, короткие notifications ниже порога и microphone noise без активности whitelisted process не считаются встречей по умолчанию.
- `Ask` mode использует prebuffer; неподтверждённый prebuffer не записывается на диск.
- Prebuffer ведётся только для разрешённых текущими настройками источников.
- Default prebuffer duration: `15 секунд`; supported values: `5`, `10`, `15`, `30` секунд.
- `Auto-discovery` of new audio-active apps отслеживает non-whitelisted playback processes above threshold and applies one of three policies: `auto-add`, `ask-to-add`, `off`.
- System processes, Windows service processes and predefined exclusions не попадают в auto-add by default.
- Устройство output и microphone может следовать system default или фиксированному выбору пользователя.
- При смене устройства во время записи система применяет одну из канонических политик: continue with new device, finish current session and start a new one, ask user if possible without data loss.
- Приложение обязано отслеживать active output devices, active microphone devices и default-device changes без необходимости перезапуска.

## Acceptance {#acceptance}

Канон корректен, если:
- supported sources, detection signals, prebuffer and device behavior описаны без опоры на исходный ТЗ-файл;
- default values and allowed options для capture/device flows явно зафиксированы;
- auto-discovery и device switching не превращаются в undefined behavior.

## Document Notes {#document-notes}

- 2026-04-02: Extracted and consolidated from the original meeting-capture TZ.
- 2026-07-11: Linked the multi-signal Ask-only detection v2 spec.
