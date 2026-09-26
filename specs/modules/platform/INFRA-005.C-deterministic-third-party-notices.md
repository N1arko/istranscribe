---
status: active
---

# INFRA-005.C: Deterministic Third-Party Notices {#root}

## Простыми словами {#plain-language}

Публичный установочный пакет содержит понятный файл с лицензиями и уведомлениями всех реально поставляемых компонентов. Он собирается без интернета из закреплённых зависимостей, проверяется вместе с MSIX и блокирует выпуск, если происхождение или условия нового файла не определены.

## Goal {#goal}

Сделать attribution/license contour Windows release полным, детерминированным и fail-closed: связать каждый redistributed package asset с проверенным notice material и включить `THIRD-PARTY-NOTICES.txt` внутрь MSIX.

## Depends on {#depends-on}

- `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#supply-chain`
- `spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission`
- `spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#release-contract`

## Scope {#scope}

### In scope {#scope.in}

- exact redistributed-package inventory из published deps graph и private-reference mapping;
- checked-in reviewed MIT, Apache-2.0 и OFL-1.1 canonical texts;
- exact-version policy override для Windows SDK private references;
- Inter font OFL supplement;
- verbatim package license/NOTICE sources с закреплёнными hashes;
- deterministic `THIRD-PARTY-NOTICES.txt` внутри MSIX;
- удаление umbrella dependency assets, которые не используются shipping runtime;
- manifest/inventory evidence и строгая package verification;
- build-only/meta/platform package exclusions с доказательством отсутствия payload.

### Out of scope {#scope.out}

- сетевое скачивание license text во время release build;
- автоматическая юридическая интерпретация произвольной лицензии;
- изменение лицензий сторонних проектов;
- пользовательский экран со всеми лицензиями в этой итерации;
- юридическое заключение для отдельной юрисдикции.

## Canonical Decisions {#decisions}

### Payload truth {#decisions.payload}

- Notice inventory строится по реально поставляемым `runtime`, `native` и `resources` assets из `IsTranscribe.Desktop.deps.json`.
- `Microsoft.Windows.SDK.NET.Ref` добавляется через exact private-reference mapping для `Microsoft.Windows.SDK.NET.dll` и `WinRT.Runtime.dll`.
- Build-only, meta и platform-inapplicable packages остаются supply-chain records, получают `redistributed=false` и не создают notice section.
- Windows audio graph содержит только нужные `NAudio.Wasapi` и transitive `NAudio.Core`; неиспользуемые NAudio umbrella/ASIO/MIDI/WinMM assemblies не поставляются.
- Package exclusion, который внезапно добавил payload, блокирует build.

### Offline policy {#decisions.policy}

- Policy keyed by exact package id, version и NuGet SHA-512.
- Expression-only MIT/Apache-2.0 packages используют reviewed canonical texts и NuSpec attribution.
- `license type=file`, `NOTICE*` и `THIRD-PARTY-NOTICES*` включаются из exact package root verbatim после path/hash validation.
- URL-only redistributed dependency блокирует release без checked-in reviewed override.
- Неизвестная license expression, изменившийся package hash или новый notice file блокируют release.

### Known overrides {#decisions.overrides}

- `Microsoft.Windows.SDK.NET.Ref/10.0.26100.84` требует exact reviewed override: local package содержит только URL и `requireLicenseAcceptance=true`, при этом два assembly входят в MSIX.
- `Avalonia.Fonts.Inter/12.1.0` требует OFL-1.1 supplement: package объявляет MIT, а шесть embedded Inter fonts содержат OFL metadata и copyright Inter Project Authors.
- Override фиксирует package/content hashes, attributed payload files, approved notice material и review reference.

### Generated notice {#decisions.output}

- Один `THIRD-PARTY-NOTICES.txt` создаётся в UTF-8 без BOM с LF endings и без timestamp.
- Package index сортируется ordinally по id/version; одинаковые source blobs дедуплицируются по SHA-256.
- Каждый redistributed package ссылается на конкретные license/notice sections.
- Файл помещается внутрь MSIX, поэтому Store handoff сохраняет текущий top-level allowlist из шести файлов.

## Release Contract {#release-contract}

- Dependency inventory различает dependency graph и redistributed payload, хранит asset mapping, copyright, `requireLicenseAcceptance`, notice strategy и source hashes.
- Release manifest хранит notice filename, SHA-256, bytes, redistributed package count и policy hash.
- Store и signed verifiers извлекают exact notice entry, подтверждают block-map coverage/hash и сверяют все redistributed packages.
- Release запрещён при пропущенной/лишней секции, unresolved URL-only license, неизвестной expression, payload from excluded package или stale override.

## Verification {#verification}

- Determinism test дважды генерирует byte-identical notice из одного lock graph.
- Mutation tests меняют package hash, asset list, notice source, expression, override и exclusion; каждый случай fail-closed.
- Fixture verification подтверждает 31 текущий redistributed package, включая exact Windows SDK private references.
- MSIX verifier подтверждает наличие notice, его hash и отсутствие отдельного непроверенного notice artifact рядом с package.
- Release, legacy, format, self-contained publish и Store metadata gates проходят после cutover.

## Acceptance {#acceptance}

1. Каждый файл сторонней зависимости внутри MSIX сопоставлен закреплённому package/version/hash.
2. Каждый из текущих redistributed packages имеет проверенную license/notice strategy.
3. Windows SDK и Inter font gaps закрыты reviewed exact-version overrides.
4. `THIRD-PARTY-NOTICES.txt` детерминированно входит в MSIX и проверяется release tooling.
5. Любая неизвестная или изменившаяся license/payload ситуация блокирует public/Store build.
6. TEST-only Store fixture и полный regression gate проходят с новым notice contract.

## Document Notes {#document-notes}

- 2026-07-12: Спека создана после payload audit MP3 Store fixture. Текущий inventory содержит 45 graph packages, MSIX получает assets от 35 packages; обнаружены offline gaps Windows SDK URL-only license и embedded Inter OFL metadata.
- 2026-07-12: После удаления unused NAudio umbrella authoritative locked graph содержит 41 package, shipping payload — 31 package. Microsoft REDIST page явно разрешает unmodified `Microsoft.Windows.SDK.NET.Ref` files как часть программы и перечисляет обе private reference assembly.
- 2026-07-12: Store fixture `2.0.0-rc.9004` содержит 31 payload package, deterministic notice и физически закреплённые NuGet archive/payload-tree hashes; strict unsigned Store verification проходит.
- 2026-07-12: Acceptance закрыт. Store и development verifiers независимо сверяют checked-in policy и payload bytes; 2-run determinism, 6 fail-closed mutations, Store metadata, 567 release, 110 legacy, 288 final contract tests и format проходят. Evidence: `artifacts/acceptance/INFRA-005.C/windows-x64/verification-summary.json`.

## External References {#external-references}

- [Microsoft: Windows SDK REDIST lists](https://learn.microsoft.com/en-us/legal/windows-sdk/redist)
- [SIL: Open Font License 1.1 official text](https://openfontlicense.org/open-font-license-official-text/)
