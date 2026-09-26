---
status: superseded
---

# INFRA-001: Windows Desktop Host Baseline {#root}

## Простыми словами {#plain-language}

Эта спека фиксирует канон Windows-host для MVP: на каком стеке живёт desktop app, как она стартует, остаётся в tray, не допускает вторую активную копию и как явно деградирует на системах без process loopback.

## Goal {#goal}

Зафиксировать готовый к реализации host-контур, на который могут опираться UI-shell, storage, capture foundation и recovery.

## Superseded by {#superseded-by}

- Release v2 UI/runtime project boundaries are governed by `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#root`; active Windows platform composition is governed by `spec://modules/platform/INFRA-009-cross-platform-repository-boundaries#root`.

## Depends on {#depends-on}

- `spec://common/PROP-001-product-canon#rules`
- `spec://common/PROP-002-app-shell-and-settings#rules`
- `spec://common/PROP-005-local-runtime-and-operations#rules`

## See also {#see-also}

- `spec://modules/app/FEAT-001-first-run-setup-and-settings#behavior`
- `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#behavior`
- `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`
- `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#decisions`
- `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#decisions`

## Scope {#scope}

### In scope {#scope.in}

- канонический desktop stack для MVP host;
- bootstrap и порядок shutdown primary app process;
- контракт single-instance и поведение second launch activation;
- правила работы в tray и жизненного цикла main window;
- правила startup entrypoint для first run, normal launch и degraded mode;
- оценка Windows capabilities и явные unsupported/degraded states;
- граница ответственности между host baseline и соседними feature/infrastructure specs.

### Out of scope {#scope.out}

- конкретные capture adapters и signal-processing internals;
- схемы SQLite, settings JSON и формата secret storage;
- точная валидация wizard fields и текстов в settings UX;
- поведение transcription pipeline;
- детали UX для recordings list.

## Decisions {#decisions}

- MVP desktop host использует один канонический stack: `WPF` на `.NET 8`.
- Host работает как tray-first и background-capable приложение; main window не владеет process lifetime.
- Для одного Windows user profile допускается только один primary instance; secondary launches лишь прокидывают activation и завершаются.
- Startup обязан учитывать capabilities и классифицировать машину как `full`, `degraded` или `blocked`.
- Отсутствие process loopback support переводит приложение в явный degraded mode, а не в silent feature breakage.
- Маршрутизация first-run и recovery-sensitive startup относится к ответственности host; детальный feature UX остаётся в соседних спеках.
- `INFRA-001` владеет process lifetime, activation и публикацией capability state; storage, capture и feature-level behavior эта спека в host layer не поглощает.

## Runtime baseline {#runtime}

- MVP desktop host работает как Windows-only `.NET 8` application, где `WPF` является каноническим UI stack.
- `WPF` выбран для MVP сознательно, потому что даёт наиболее стабильный baseline для tray residency, управления single-process lifetime и Windows interop, не создавая второй host contour.
- Host является background-capable: приложение может работать без видимого main window, пока tray, watchers и background jobs продолжают жить.
- Для одного Windows user profile допускается только один running host instance. Разные Windows users на одной машине не разделяют process instance, settings и secrets.

## Capability model {#capability-model}

### Capability states {#capability-model.states}

- Host во время bootstrap классифицирует текущую машину в одно из трёх состояний:
  - `full`: Windows 11 или Windows 10 build `20348+`; доступен полный MVP contour.
  - `degraded`: desktop host может работать, но process loopback недоступен; device loopback, microphone capture и manual flows остаются доступны.
  - `blocked`: машина не удовлетворяет минимальным host prerequisites, и приложение должно остановиться с понятным пользователю объяснением вместо частично запущенного состояния.

### Capability rules {#capability-model.rules}

- Process loopback support является единственным capability gap, который по умолчанию допускается переводить в MVP degraded mode.
- Отсутствие process loopback должно отключать или скрывать только те behaviors, которые реально на него опираются; остальная часть приложения не должна silently ломаться.
- В degraded mode пользователю по-прежнему доступны:
  - device loopback capture;
  - microphone capture;
  - `Force Record`;
  - tray controls;
  - first-run setup, settings and recordings access.
- В degraded mode process-specific auto-capture недоступен. Features, которые зависят от process loopback, должны получать явный capability flag, а не выводить его ad hoc.
- Ошибка инициализации tray icon, primary host windowing stack или single-instance coordination является `blocked` startup error, а не degraded mode.
- Ошибка инициализации optional capture-specific services является non-fatal только если приложение всё ещё может войти в честный degraded state.

## Bootstrap lifecycle {#bootstrap}

### Primary startup sequence {#bootstrap.sequence}

Канонический startup order такой:

1. Запустить process и поднять минимальный crash-safe logging.
2. Определить per-user app identity, нужную для single-instance coordination.
3. Оценить host capabilities и классифицировать машину как `full`, `degraded` или `blocked`.
4. Захватить primary-instance lock и activation channel.
5. Если process является secondary launch, прокинуть activation request в primary instance и завершиться cleanly.
6. Инициализировать tray icon и минимальный набор tray commands.
7. Инициализировать persistence, settings loading и recovery/diagnostics coordinators.
8. Инициализировать background observers и services в режиме `full` или `degraded`.
9. Определить первую видимую surface: first-run wizard, recovery-required UI или отсутствие окна.

### Startup invariants {#bootstrap.invariants}

- Приложение не должно запускать watchers, capture services или background jobs до того, как установлено primary-instance ownership.
- После прохождения bootstrap primary host не должен оставаться running без tray; tray является основной operational surface MVP.
- Загрузка settings/storage происходит после single-instance arbitration и до того, как feature surfaces становятся interactive.
- Если startup падает после того, как process стал primary, приложение обязано освободить частично созданные host resources и либо показать blocking error, либо завершиться cleanly; zombie background process оставаться не должен.

## Single-instance contract {#single-instance}

- Desktop host является single-instance в пределах одного Windows user profile.
- Secondary launch никогда не должен мутировать storage, запускать watchers или создавать второй tray icon.
- Поведение secondary launch такое:
  - отправить сигнал primary instance через local activation channel;
  - запросить foreground activation нужной surface;
  - завершиться без user-visible error.
- Default activation intent для обычного user launch: `open-or-focus-app`.
- Если onboarding не завершён, `open-or-focus-app` должен открывать first-run wizard, а не обычное shell window.
- Если primary instance уже намеренно завершает работу, он может отклонить поздние activation requests; secondary process всё равно завершается, а не пытается стать второй live instance.

## Tray and window lifecycle {#tray-window-lifecycle}

### Tray baseline {#tray-window-lifecycle.tray}

- Tray residency обязательна для running MVP host.
- Tray icon должен существовать на протяжении всего lifetime primary instance, кроме финальной shutdown phase.
- Семантика tray commands для recording и privacy controls регулируется `PROP-002` и `FEAT-003`; эта спека регулирует именно жизненный цикл самой tray surface.

### Window rules {#tray-window-lifecycle.windows}

- Main window не является владельцем process lifetime. Закрытие последнего видимого окна по умолчанию не должно завершать приложение.
- Канонический default: `minimize to tray on close = true`.
- Host обязан уметь повторно открывать main window из tray, через second-launch activation или другими in-process commands без перезапуска process.
- Завершать host process может только явная команда `Quit` или фатальная startup/runtime failure.

### Quit semantics {#tray-window-lifecycle.quit}

- `Quit` всегда проходит через orderly shutdown coordinator.
- Host обязан освобождать tray resources, снимать transient OS integrations и останавливать background services в детерминированном порядке.
- Host не должен hard-kill активную запись или recovery-critical finalization path без явного user intent. Если из текущего состояния нельзя выйти безопасно, quit request должен быть явно подтверждён, отменён или отложен.
- Точный recording outcome при quit регулируется session-level спеками; host baseline регулирует сам факт того, что shutdown является coordinated, а не abrupt.

## Startup entrypoints {#entrypoints}

### First run {#entrypoints.first-run}

- First-run setup является каноническим host entrypoint, а не отдельным utility mode.
- Если обязательный onboarding не завершён, primary instance обязан открыть first-run wizard вместо того, чтобы молча оставаться в tray-only mode.
- Конкретные wizard steps и settings fields принадлежат `FEAT-001`; host baseline принадлежит правило, что незавершённый onboarding блокирует normal idle startup.

### Normal launch {#entrypoints.normal}

- При явном user launch с завершённым onboarding приложение может открыть обычное shell window или сфокусировать уже работающее.
- При autostart с завершённым onboarding приложение стартует в tray-first mode, если только recovery flow не требует немедленного внимания пользователя.

### Recovery-sensitive launch {#entrypoints.recovery}

- Если startup recovery обнаруживает незавершённую работу, требующую выбора пользователя, host bootstrap обязан показать соответствующее окно, а не прятать проблему в логах.
- Сама recovery policy определяется `INFRA-004`; эта спека регулирует только требование, что recovery может влиять на начальную видимую surface.

## Host service boundaries {#service-boundaries}

### Host-owned responsibilities {#service-boundaries.host}

`INFRA-001` owns:

- process lifetime и порядок shutdown;
- single-instance coordination;
- работу в tray и window activation;
- capability assessment и публикацию capability state;
- startup routing между first run, normal launch и recovery-required launch;
- канонический выбор desktop runtime stack.

### Neighbor-owned responsibilities {#service-boundaries.neighbors}

Эта спека не владеет:

- settings catalog и содержимым wizard: `spec://modules/app/FEAT-001-first-run-setup-and-settings#behavior`;
- значением tray commands, hotkeys и current-recording controls: `spec://modules/app/FEAT-003-manual-recording-controls-and-tray#behavior`;
- settings/storage schema и secret persistence: `spec://modules/platform/INFRA-002-local-persistence-and-secret-storage#decisions`;
- capture adapters, process watchers и prebuffer primitives: `spec://modules/platform/INFRA-003-windows-audio-capture-foundation#decisions`;
- retry/recovery logic и diagnostic jobs: `spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#decisions`.

Реализация не должна заново переопределять эти границы ad hoc внутри host layer.

## Acceptance {#acceptance}

Эта спека достаточно полна для реализации, если одновременно выполняются все условия:

- MVP desktop stack не оставляет двусмысленности: `.NET 8 + WPF` и один host contour.
- Primary startup order описан достаточно явно, чтобы single-instance, tray и settings bootstrap не гонялись друг с другом.
- Поведение second launch детерминировано и не допускает вторую live tray instance.
- Правила tray и window lifetime описаны достаточно явно, чтобы реализовать `Open app`, close-to-tray и `Quit` без гадания о process ownership.
- Capability gating различает startup outcomes `full`, `degraded` и `blocked`, а отсутствие process loopback трактуется как явный degraded mode, а не silent failure.
- Маршрутизация first-run и recovery-sensitive startup определена как host concern, а соседние спеки сохраняют ownership над своим поведением.
- Scope boundaries с `FEAT-001`, `FEAT-003`, `INFRA-002`, `INFRA-003` и `INFRA-004` явные и не пересекаются.

## Document Notes {#document-notes}

- 2026-04-02: Сформирован начальный baseline host-спеки на основе MVP TZ и bootstrap-задачи репозитория.
- 2026-04-03: Спека переписана в готовый к реализации host-канон с явным выбором runtime, порядком bootstrap, capability model и границами ответственности.
- 2026-04-03: Основной prose и acceptance wording переведены на русский; английский оставлен только для устоявшихся technical terms, literals и cross-spec structure.
- 2026-07-11: Linked the .NET 10/Avalonia release v2 architecture replacement.
