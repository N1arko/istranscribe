---
status: superseded
---

# PROP-001: Product Canon {#root}

## Простыми словами {#plain-language}

Этот документ фиксирует, что именно строится в MVP: личное Windows tray-приложение, которое автоматически или полуавтоматически фиксирует встречи, сохраняет аудио локально и выпускает Markdown-транскрипт через Fireworks.

## Goal {#goal}

Задать общие продуктовые границы, которые govern все модульные `PROP`, `FEAT` и `INFRA`-спеки.

## Superseded by {#superseded-by}

- Для release v2 продуктовый канон заменён `spec://common/PROP-006-release-v2-product-canon#root`. Этот документ сохраняет исторический MVP/v1 срез.

## Scope {#scope}

### In scope {#scope.in}

- целевая платформа и пользователь;
- обязательные пользовательские режимы MVP;
- канонические входы и выходы системы;
- жёсткие non-goals первой волны;
- verification and MVP acceptance bar.

### Out of scope {#scope.out}

- реализация конкретных экранов;
- низкоуровневые детали аудиозахвата;
- детальный контракт Fireworks API.

## Rules {#rules}

- MVP остаётся personal-first и local-first: без серверной части, без аккаунтов и без синхронизации между устройствами.
- Проект планируется как open-source продукт с публичным GitHub-репозиторием.
- Поддерживаемая платформа MVP: Windows 11 и Windows 10 build `20348+`; на менее подходящих системах приложение не должно падать, но process loopback auto-capture может деградировать до device loopback и manual flows.
- Канонический пользовательский результат каждой завершённой сессии: локально сохранённые аудиоартефакты и отдельный `.md`-файл транскрипции.
- Продукт обязан поддерживать режимы `Off`, `Ask`, `Auto`, `Privacy Pause` и `Force Record`; эти режимы не считаются опциональными.
- Fireworks является единственным транскрипционным backend в MVP; `.md` собирается локально из ответа API.
- Секреты и чувствительные локальные артефакты не покидают устройство пользователя, кроме явной отправки аудио в Fireworks на транскрибацию.
- Нефункциональный инвариант MVP: потеря записанного аудио при ошибке транскрибации или пропаже устройства недопустима как нормальное поведение.
- В MVP не входят: командная работа, backend/accounts, billing, CRM integrations, semantic search, transcript editor, multi-device sync и версии для macOS/Linux.
- Публичный repo-level канон должен быть достаточно самодостаточным для внешнего читателя и потенциального контрибьютора; критичные product и system decisions не должны жить только в устных пояснениях.

## Verification {#verification}

### Functional scenarios {#verification.functional}

Minimum functional scenarios:
1. Auto starts when a whitelisted app such as Zoom becomes audio-active.
2. Ask mode with positive confirmation.
3. Ask mode with refusal.
4. Manual Force Record.
5. Microphone-only capture.
6. Output-device-only capture.
7. Process capture for a whitelisted browser.
8. Default output device switch.
9. Microphone disconnect during recording.
10. Auto switch to a new microphone after headset connection.
11. Auto-discovery of new audio-active apps with automatic whitelist add.
12. Auto-discovery of new audio-active apps with ask-to-add policy.
13. Invalid or unavailable Fireworks API key.
14. Network failure during transcription.
15. Retry after transcription failure.
16. Recovery after unexpected app shutdown.

### Non-functional scenarios {#verification.non-functional}

Minimum non-functional scenarios:
1. Background work for `8+ часов`.
2. Multiple meetings sequentially during one day.
3. Large audio files.
4. Several headset and USB microphone combinations.

## Acceptance {#acceptance}

Проект соответствует этому `PROP`, если:
- каждая модульная спека не выходит за пределы personal/local-first MVP;
- продуктовые режимы и итоговые артефакты согласованы между модулями;
- unsupported scenarios оформлены как деградация, а не как неявный краш или silent failure;
- пользователь может установить приложение и пройти first-run setup;
- режимы `Off`, `Ask`, `Auto`, `Privacy Pause` и `Force Record` ведут себя канонически;
- приложение умеет записывать минимум один output source и один microphone source;
- automatic start by whitelisted app and audio activity works;
- Ask mode uses prebuffer and confirmation;
- completed recording is saved without loss;
- Fireworks API key is configurable and used for transcription;
- app-rule discovery policy is configurable;
- microphone auto-detection and handling of input-device changes work;
- diarization can be turned on and off;
- transcript is saved as `.md`;
- failed transcription can be retried;
- user can open recording artifacts from the app;
- the app does not crash on device loss or provider error.

## Document Notes {#document-notes}

- 2026-04-02: Initial product canon authored from the original MVP technical brief.
- 2026-04-02: Merged MVP verification and acceptance canon into the main product canon.
- 2026-07-11: Linked the release v2 superseding product canon.
