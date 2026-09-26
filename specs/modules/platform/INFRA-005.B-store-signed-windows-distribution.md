---
status: active
---

# INFRA-005.B: Store-Signed Windows Distribution {#root}

## Простыми словами {#plain-language}

Пользователь устанавливает isTranscribe через обычную карточку Microsoft Store. Microsoft подписывает MSIX своей доверенной подписью, управляет сертификатом и доставляет обновления. Разработчику не нужно покупать code-signing certificate, а пользователю — доверять локальный сертификат, открывать PowerShell или обходить предупреждение SmartScreen.

## Goal {#goal}

Перевести публичный Windows x64 release на бесплатно подписываемый Microsoft Store MSIX, сохранив текущий self-contained payload, packaged capabilities и данные пользователя.

## Depends on {#depends-on}

- `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#root`
- `spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#root`
- `spec://modules/app/FEAT-013-minimal-desktop-experience#tray`
- `spec://modules/app/FEAT-012.A-royalty-cleared-mp3-artifact#root`
- `spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#root`
- `spec://common/PROP-006-release-v2-product-canon#platform.windows-release`

## Supersedes {#supersedes}

Эта change-спека заменяет public-distribution и production-signing решения из:

- `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#package-decision`;
- `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#signing`;
- Store exclusion из `spec://modules/platform/INFRA-005.A-production-windows-x64-installer#scope.out`.

Остальные package, persistence, capability и lifecycle требования `INFRA-005.A` сохраняются.

## Scope {#scope}

### In scope {#scope.in}

- Microsoft Store MSIX как основной публичный Windows x64 release;
- Store-assigned package identity, publisher и application identity;
- Store-managed trusted signing и certificate lifecycle;
- Partner Center submission artifact и release metadata;
- карточка Store, privacy/support links и публичная ссылка установки;
- Store-managed update поверх установленной версии;
- миграция существующих данных из канонического `%LocalAppData%\isTranscribe`;
- private-flight acceptance перед публичной публикацией;
- development-signed MSIX только для локального engineering acceptance.

### Out of scope {#scope.out}

- покупка OV/EV code-signing certificate;
- Azure Artifact Signing для первой публичной волны;
- публичный unsigned/self-signed EXE, MSI или MSIX download;
- offline installer для пользователей без Microsoft Store;
- enterprise deployment;
- macOS distribution.

## Canonical Decisions {#decisions}

### Public entrypoint {#decisions.entrypoint}

- Основной пользовательский entrypoint — карточка isTranscribe в Microsoft Store.
- Product website и release announcements ведут на Store listing или системный Store install surface.
- Normal install не требует terminal, certificate import, developer mode или administrator approval.
- Приложение появляется в Start Menu и Installed Apps через зарегистрированный Store MSIX.

### Signing and trust {#decisions.signing}

- Submission использует MSIX contour; Microsoft Store переподписывает пакет после certification и управляет доверенной подписью.
- Production private key и купленный certificate не являются зависимостями release pipeline.
- Self-signed development certificate остаётся локальным тестовым инструментом. Он не публикуется как пользовательская trust-инструкция.
- Release tooling формирует Store submission package без development signature и проверяет Store identity metadata до отправки.
- Store submission не объявляет `unvirtualizedResources`, `desktop6:FileSystemWriteVirtualization` и `desktop6:RegistryWriteVirtualization`: Microsoft относит этот capability к строго ограниченным сценариям и указывает, что он не предназначен для обычных desktop apps.
- `runFullTrust` сохраняется для packaged desktop process и получает явное обоснование на Submission options page.

### Developer account {#decisions.account}

- Регистрация нового Individual или Company developer account начинается через `storedeveloper.microsoft.com` и не требует registration fee в актуальном onboarding flow.
- Account type выбирается по публичному publisher identity: собственное имя для Individual либо зарегистрированное юридическое лицо для Company.
- Identity/business verification остаётся обязательным внешним шагом Microsoft.

<!-- REVIEW: пользователь должен выбрать Individual или Company account и передать Store-assigned identity/publisher values после резервирования имени. -->

### Package identity {#decisions.identity}

- Store package name, publisher, family name и application identity берутся из Partner Center и фиксируются в release identity policy.
- Эти значения стабильны между Store releases.
- Development identity `CN=isTranscribe Development` остаётся отдельной и не используется в production submission.
- Канонические settings, secrets, database, logs и recordings остаются вне replaceable package payload. Store install обнаруживает существующий `%LocalAppData%\isTranscribe` и применяет поддерживаемые миграции.
- Store flight подтверждает актуальное Windows full-trust pass-through поведение для `%LocalAppData%\isTranscribe`, включая update, uninstall и reinstall. Публичная публикация запрещена, если фактическое поведение расходится с persistence contract.
- Packaged StartupTask и single-instance behavior проверяются повторно под Store identity.

### Updates and rollback {#decisions.updates}

- Microsoft Store доставляет новую версию поверх предыдущей package identity.
- App data переживают update и default uninstall согласно `INFRA-005.A`.
- Package version монотонно растёт; downgrade не является пользовательским happy path.
- Rollout может использовать private audience и staged publication controls Partner Center.

### Direct-download boundary {#decisions.direct-download}

- Development MSIX может передаваться ограниченному тестеру вместе с явным временным certificate-trust flow.
- Такой artifact маркируется test-only и public-ineligible.
- Публичный сайт не выдаёт development MSIX как основной installer.
- Отдельный direct-download production installer потребует новую change-спеку и доверенный signing contour.

## Submission Contract {#submission}

- Build принимает Store identity values через проверяемый release configuration, без private signing material.
- Submission output содержит MSIX/MSIXUpload-compatible payload, semantic version, package version, checksums, dependency/license inventory и release manifest.
- Manifest не содержит development publisher или unresolved template tokens.
- Manifest не содержит Store-ineligible `unvirtualizedResources` и связанных virtualization declarations.
- Partner Center metadata хранится в отдельном fail-closed JSON: шаблон имеет `configured=false`, а проверенный handoff криптографически связывает listing с semantic version и SHA-256 конкретного Store MSIX.
- Private flight использует `Free`, все рынки, private audience, direct-link discoverability и manual publishing hold. Изменение этих значений требует отдельного review перед submission.
- Partner Center listing содержит RU/EN product name, description, short description, features, keywords, privacy policy URL, support contact и system requirements.
- Для каждого языка подготовлены четыре детерминированных PNG production UI без персональных данных. Каждый файл не превышает 50 MB и имеет обе стороны не меньше Store desktop minimum `1366 × 768`.
- Privacy disclosures соответствуют локальной audio capture модели и фактическому отсутствию active transcription network path.
- Privacy policy проходит содержательный review, получает публичный HTTPS URL и доступна пользователю из карточки Store и приложения до публичной публикации.
- Age-rating questionnaire и certification notes для `runFullTrust` заполнены до handoff; unresolved placeholders, тестовые URL и TEST-only review values запрещены в реальном submission.
- Read-only metadata verifier проверяет точную схему, лимиты листинга, screenshot allowlist/PNG dimensions, privacy facts, review gates и привязку к уже проверенному Store artifact.

## Verification {#verification}

### Local engineering gate {#verification.local}

- Development-signed `2.0.0 → 2.0.1 → downgrade rejection → uninstall → reinstall` lifecycle проходит на текущем Windows host с временным exact-certificate trust.
- Package, process и temporary trust отсутствуют после cleanup.
- Settings, secrets, SQLite rows, recordings metadata и retained backup проходят integrity gates.
- Packaged tray, single-instance, manual recording и process-loopback проверяются отдельно до Store submission.

### Store flight gate {#verification.store-flight}

1. Зарезервировать product name и получить Store identity values.
2. Собрать submission payload с production Store identity.
3. Заполнить и проверить metadata handoff, privacy URL, support contact, age rating и RU/EN screenshots.
4. Опубликовать private flight/private audience build с manual publishing hold.
5. Установить приложение из Store surface на обычном Windows x64 профиле без development certificate.
6. Проверить Start Menu, Installed Apps, tray, single-instance, StartupTask и manual recording.
7. Доставить более новую flight-версию и проверить сохранность данных.
8. Удалить приложение, подтвердить default data preservation и переустановить из Store.
9. Проверить Microsoft signature и отсутствие SmartScreen/certificate prompts в normal install flow.
10. Подтвердить отсутствие `unvirtualizedResources` в сертифицированном manifest и сохранность канонических данных на фактическом Store package.

## Acceptance {#acceptance}

INFRA-005.B завершена, когда:

1. Microsoft Store MSIX зафиксирован как единственный основной public Windows entrypoint.
2. Store identity внесена в release policy и package manifest без development publisher.
3. Submission artifact автоматически собирается без production certificate secret.
4. Metadata verifier принимает только review-complete listing, privacy и screenshot handoff, привязанный к конкретному проверенному MSIX.
5. Private Store flight проходит install/update/uninstall/reinstall и packaged-capability acceptance.
6. Пользователь устанавливает приложение из Store без terminal, certificate trust и покупки разработчиком code-signing certificate.
7. Website/release link открывает публичную Store listing.
8. Development MSIX остаётся явно test-only и исключён из public handoff.

## Document Notes {#document-notes}

- 2026-07-12: Change-spec authored after product decision to exclude purchased code-signing certificates. Microsoft documentation current on this date states that Store MSIX signing is free and handled by Microsoft, while new Individual and Company Store developer onboarding has no registration fee through `storedeveloper.microsoft.com`.
- 2026-07-12: Store contour исключил `unvirtualizedResources`: Microsoft capability documentation ограничивает его специальными сценариями и требует отдельного Store approval, а актуальный MSIX container contract указывает pass-through для full-trust writes в user profile. Private flight остаётся обязательным фактическим gate для persistence.
- 2026-07-12: Реализован certificate-free `SigningMode Store`, fail-closed identity template и отдельный verifier. TEST-ONLY fixture `2.0.0-rc.9001` имеет MSIX SHA-256 `7d7d1599e87e68b0e472f54da3c54884e1100e316831887d0bde650aded2032e`, `Authenticode=NotSigned`, 249 ZIP entries, 247 block-map files и 2318 проверенных SHA-256 blocks. Verifier SHA-256: `d79a76cf9dfa1b67f2940134bc7109f82b80c1a39507a6cab549c9cd5a0f48f1`; Store tooling contract tests 5/5 и полный release suite 557/557 проходят. Fixture использует тестовые Partner Center identity/AAC values и не является настоящим submission.
- 2026-07-12: Добавлены fail-closed Partner Center metadata/privacy templates, read-only metadata verifier SHA-256 `085535feff10b11c1bee37384458f1c21503ba504fac3e40b56e1f2831e63f62` и отдельный deterministic Store capture. TEST-ONLY fixture связывает RU/EN listing, exact 8 PNG и `runFullTrust` notes с MSIX `2.0.0-rc.9001`; screenshot manifest SHA-256 `f9a7633a566cef3c02318f2b555747775bfc2fba2820f28b32853311d87c5b70`. Main screenshots имеют `1380 × 1860`, Ask — `1960 × 890`; все TrueColor/opaque, до 50 MB и с проверяемыми hashes. В Help добавлен локализованный privacy surface, проверенный live в dark theme с прокруткой. Positive fixture и два negative fail-closed gates проходят; полный release suite 566/566, legacy 110/110 и format verify зелёные. Настоящий submission всё ещё требует Partner Center identity, hosted reviewed URLs, age rating и AAC review.

## External References {#external-references}

- [Microsoft: App capability declarations](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations)
- [Microsoft: MSIX containerization overview](https://learn.microsoft.com/en-us/windows/msix/msix-containerization-overview)
- [Microsoft: Flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization)
- [Microsoft: Create an app submission for an MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission)
- [Microsoft: Add and edit Store listing info](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info)
- [Microsoft: Screenshots and images](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images)
- [Microsoft: Microsoft Store Policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
