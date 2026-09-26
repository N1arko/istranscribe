---
status: active
---

# FEAT-011.A: Consent-Aware Windows Validation And Zen Support {#root}

## Простыми словами {#plain-language}

Поддержка сервиса не должна требовать специально созваниваться в каждом клиенте. Все
заявленные приложения проходят одинаковые детерминированные проверки, установленная
среда проверяется без входа во встречу, а живое подтверждение собирается во время
обычного созвона или после отдельного разрешения пользователя. Основной браузер текущего
Windows-релиза — Zen Browser.

## Goal {#goal}

Заменить обязательную постановочную live-матрицу FEAT-011 на consent-aware validation,
закрепить Zen Browser как поддерживаемый Firefox-family host и сохранить честное
различие между совместимостью по контракту и наблюдённым живым сценарием.

## Depends on {#depends-on}

- `spec://modules/app/FEAT-011-meeting-detection-v2#root`
- `spec://common/PROP-006-release-v2-product-canon#detection-principles`

## See also {#see-also}

- `spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#root`

## Supersedes {#supersedes}

- live-matrix completion requirement from `spec://modules/app/FEAT-011-meeting-detection-v2#verification`;
- client-specific live-evidence requirement from `spec://modules/app/FEAT-011-meeting-detection-v2#acceptance`;
- former mandatory staged Zoom and Контур.Толк scenarios from `spec://modules/platform/INFRA-008-release-hardening-and-windows-acceptance#e2e`.

## Scope {#scope}

### In scope {#scope.in}

- deterministic profile, scoring, window/UIA and process-tree fixtures for every initial service;
- explicit `zen.exe` browser-family support;
- Firefox-style same-name process-family normalization;
- disjoint attribution when one watched application launches another;
- safe installed-process discovery with version and executable hash;
- an optional real-call evidence tier that requires normal user activity or explicit consent;
- accurate reporting of unobserved and unavailable live scenarios.

### Out of scope {#scope.out}

- staging a Zoom or Контур.Толк meeting solely for acceptance;
- claiming a client-specific live pass from fixtures or process discovery;
- weakening privacy, false-positive, timing or Ask-only behavior;
- recording and artifact finalization owned by `FEAT-012`.

## Validation Levels {#validation-levels}

Each profile/surface can have one of three cumulative evidence levels:

1. `contract_verified` — current-build fixtures prove process matching, privacy-reduced
   window/UIA rules, scoring and candidate lifecycle.
2. `environment_observed` — a safe preflight additionally records the installed
   executable name, version and SHA-256 without joining a meeting or starting capture.
3. `live_verified` — a real meeting produces the expected Ask prompt and privacy gates
   under the existing runner. This level is collected only during an ordinary user call
   or after explicit authorization.

The product documentation and evidence index must preserve the exact level. A lower
level cannot be described as a live pass.

`artifacts/acceptance/FEAT-011/windows-x64/compatibility-index.json` is the
machine-readable source for these levels. It binds current-build production matcher,
assembler and scorer checks to the latest integrity-checked live/preflight row; its
SHA-256 is stored alongside it.

## Zen Browser Contract {#zen}

- Zen is matched by normalized process name `zen.exe` for Teams browser, Google Meet,
  Яндекс Телемост, Контур.Толк and the generic browser fallback.
- Zen uses the Firefox-family multi-process model. Same-name descendants form one
  logical process family rooted at the outermost live `zen.exe`.
- A nested watched application with another executable starts a separate process family;
  parent and nested families are disjoint for audio attribution.
- Window/UI Automation evidence and process-output capture bind to the same family root.
- Process-output capture includes the target process tree.
- Common call controls include Firefox/Zen-accessible names such as `Turn on microphone`,
  `Turn off microphone` and their Russian equivalents.

## Consent-Aware Release Policy {#release-policy}

- FEAT-011 completion does not require staged Zoom or Контур.Толк calls.
- The eight-row machine-readable live matrix remains diagnostic and fail-closed: an
  unobserved row stays `not_run` or `blocked` and never becomes `passed` implicitly.
- FEAT-011 can complete with current-build `contract_verified` evidence for all initial
  profiles, safe environment evidence for Zen and the original multi-signal quality gates.
- Final `INFRA-008` end-to-end detection evidence may use one naturally occurring,
  user-authorized meeting in any supported service. Zen is preferred when it reflects
  the user's real environment.
- Remaining named services retain `contract_verified` support and are listed with their
  actual evidence level in release evidence.

## Acceptance {#acceptance}

This change is complete when:

1. `zen.exe` is present in the shared browser process registry and watched-process set.
2. Same-name browser descendants collapse to one root, while nested different watched
   applications remain separate and receive their own audio sessions.
3. A Zen + Google Meet fixture reaches one stable Ask candidate through the production
   Windows matcher and Core scorer.
4. Browser playback and generic-browser negative gates remain unchanged.
5. A safe Zen preflight observes x64 Windows, `zen.exe`, client version and executable hash
   while remaining `not_run: operator_consent_missing` and without initializing the live runtime.
6. All FEAT-011 automated tests and formatting checks pass in Release configuration.
7. The live matrix reports missing consent/client outcomes accurately and is not used as
   the FEAT-011 completion switch.
8. `BOARD`, `WAL`, base FEAT-011 and downstream INFRA-008 reference this policy.

## Document Notes {#document-notes}

- 2026-07-12: Added a current-build compatibility index with all eight declared surfaces: seven are `environment_observed`, Контур.Толк desktop is `contract_verified`, all contract scenarios pass, and every browser row uses installed Zen 1.21.6b. Live status remains separate and incomplete by design.
- 2026-07-12: Created from the user's decision to avoid staged Zoom and Контур.Толк calls and to support Zen Browser as the primary Windows browser. The distinction between deterministic compatibility and live-observed behavior is intentionally preserved.
