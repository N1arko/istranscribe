---
status: active
---

# FEAT-013.A: Floating Recording Widget {#root}

## Простыми словами {#plain-language}

Во время встречи isTranscribe показывает небольшой виджет записи поверх рабочих окон. Он подтверждает активную запись, показывает фактическую длительность, даёт быстро поставить запись на паузу или завершить её и может находиться в удобном пользователю месте экрана.

## Goal {#goal}

Добавить к release-v2 desktop shell компактный always-on-top виджет активной записи в выбранном направлении «Боковой маркер».

## Depends on {#depends-on}

- `spec://modules/app/FEAT-013-minimal-desktop-experience#visual-system`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#primary-window.states`
- `spec://modules/app/FEAT-012-recording-artifact-pipeline-v2#finalization`
- `spec://modules/app/FEAT-010.A-release-v2-localization#root`

## Scope {#scope}

### In scope {#scope.in}

- отдельная компактная Avalonia surface для активной и приостановленной записи;
- закрепление у правого края рабочей области активного монитора;
- topmost, tray-first и taskbar-free lifecycle;
- pause-aware таймер, pause/resume и finish actions из текущего runtime;
- свёрнутое состояние с видимым индикатором записи;
- перетаскивание за свободную область и сохранение выбранной позиции между записями и перезапусками;
- light, dark, system и high-contrast темы;
- ru/en localization и desktop accessibility;
- визуальная сверка с выбранным mockup.

### Out of scope {#scope.out}

- автоматическое завершение записи по окончании встречи;
- постоянный idle-виджет при отсутствии записи;
- финальная macOS-specific реализация;
- управление транскрибацией.

## Surface Behavior {#surface}

- Виджет появляется только при `ApplicationActivityState.Recording` с активной meeting session; пауза остаётся состоянием той же записи.
- Новая session открывает виджет в развёрнутом состоянии на мониторе с текущим рабочим контекстом.
- При первом запуске виджет появляется у правого края working area примерно по вертикальному центру и не занимает место в taskbar.
- Поверхность остаётся topmost и не открывает основное окно при показе.
- После перехода runtime из записи в processing, ready, attention или listening поверхность скрывается.
- При следующей записи поверхность появляется в последней сохранённой позиции и проверяет её по актуальным рабочим областям мониторов.

## Expanded State {#expanded}

Развёрнутая surface содержит слева направо:

1. спокойный recording indicator;
2. активную длительность записи;
3. icon-only pause/resume action;
4. icon-only finish action;
5. icon-only collapse action.

Визуальные правила:

- размеры и плотность следуют выбранному mockup `exec-3ac615bc-6be7-4835-be37-4c20c7ff1fb2.png`;
- применяются токены Calm Instrument, Inter и существующая геометрия controls;
- recording использует `Brush.Status.Recording`, pause использует warning role;
- pointer-over и pressed состояния меняют тон в пределах соответствующей semantic role;
- controls имеют tooltip, automation name и доступную область взаимодействия.

## Collapsed State {#collapsed}

- Collapse уменьшает surface до edge marker с recording или paused indicator.
- Edge marker поддерживает свободное перетаскивание за всю свою поверхность.
- Нажатие на marker возвращает развёрнутое управление.
- Состояние collapse сохраняется в пределах текущей session.
- Новая session всегда начинается развёрнутой.

## Placement And Dragging {#placement}

- В развёрнутом состоянии свободная область с индикатором и таймером служит drag handle; нажатия на action buttons продолжают выполнять их команды.
- В свёрнутом состоянии весь edge marker служит drag handle; короткое нажатие продолжает раскрывать управление.
- Drag handle показывает системный курсор перемещения и не добавляет отдельную визуальную кнопку.
- После завершения перетаскивания позиция ограничивается рабочей областью выбранного монитора с небольшим внешним отступом.
- Выбранные координаты сохраняются локально отдельно от геометрии основного окна.
- Позиция сохраняется между meeting sessions и перезапусками приложения.
- Если сохранённый монитор исчез или его working area изменилась, координаты переносятся в ближайшую доступную рабочую область.
- Collapse и expand сохраняют центр виджета настолько, насколько позволяет текущая рабочая область.

## Timer And Actions {#controls}

- Таймер использует тот же active-duration contract, что primary window.
- Во время паузы значение остаётся замороженным; после resume продолжает отсчёт от сохранённой active duration.
- Pause/resume и finish вызывают те же runtime commands, что primary window и tray.
- Busy state временно блокирует повторные действия.
- Finish закрывает виджет после подтверждённого runtime transition из recording.

## Lifecycle And Accessibility {#lifecycle}

- Один process показывает не больше одной recording widget surface.
- Surface корректно скрывается и освобождается при shutdown.
- Геометрия, drag clamping и restore учитывают DPI scaling и working area выбранного экрана.
- Вся пользовательская копия живёт в локализационных ресурсах.
- Icon-only actions имеют доступные имена, tooltip и keyboard focus.
- High contrast сохраняет различимость состояния и controls.

## Verification {#verification}

- Controller tests покрывают show, pause persistence, session reset, hide и dispose lifecycle.
- View-model tests подтверждают pause-aware timer и paused/running presentation flags.
- Placement tests покрывают координаты внутри working area, clamping у каждой границы и перенос устаревшей позиции на доступный экран.
- Release build и Desktop tests проходят.
- Реальная Windows surface проверяется в recording и paused states: topmost, initial right edge, drag, restore, collapse/expand, pause/resume и finish.
- Реализация и выбранный mockup проходят визуальную сверку с фиксацией результата в `design-qa.md`.

## Acceptance {#acceptance}

FEAT-013.A завершена, когда:

1. Активная запись показывает один виджет поверх рабочего монитора; первый показ использует правый край.
2. Таймер совпадает с active duration и останавливается на паузе.
3. Pause/resume, finish и collapse/expand работают из виджета.
4. Виджет перетаскивается в развёрнутом и свёрнутом состояниях, сохраняет позицию и остаётся доступным после изменений конфигурации мониторов.
5. Виджет скрывается после окончания записи и при shutdown.
6. Themes, scaling, localization и accessibility сохраняют пригодный интерфейс.
7. Desktop verification и визуальная сверка проходят.

## Document Notes {#document-notes}

- 2026-08-19: свёрнутый edge marker получил свободное перетаскивание с сохранением короткого нажатия для раскрытия.
- 2026-07-30: Пользователь подтвердил рабочий native widget; design QA passed,
  Release Desktop tests 127/127.
- 2026-07-14: выбран второй из трёх визуальных вариантов — edge-anchored expanded recording strip с компактным collapsed marker.
- 2026-07-14: по пользовательской проверке добавлены свободное перетаскивание и persisted placement в текущий активный scope.
