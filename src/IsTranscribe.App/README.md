# Legacy WPF migration fallback

`IsTranscribe.App` сохраняется только для проверки совместимости и отката во время release-v2 migration.

- проект не входит в `isTranscribe.sln` и не является release artifact;
- Windows x64 release публикуется из `IsTranscribe.Desktop`;
- регрессионные тесты fallback запускаются через `isTranscribe.Legacy.sln`;
- новая продуктовая логика и UI в этот проект не добавляются.

@spec spec://modules/platform/INFRA-007-cross-platform-core-and-avalonia-shell#acceptance
