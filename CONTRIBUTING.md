# Contributing to isTranscribe

Bug reports, documentation fixes, translations, and code contributions are welcome. Issues and pull requests can be written in English or Russian.

## Reporting a problem

Include the OS version, the app version or commit, steps to reproduce, and the result you expected. A small synthetic audio sample is useful for transcription problems. Review any logs before uploading them; recordings, transcript contents, provider keys, and personal paths can be sensitive.

Security reports go through the [private reporting route](SECURITY.md).

## Sending a change

1. For a substantial feature or platform change, open an issue so the scope can be discussed.
2. Fork the repository and create a branch for one focused change.
3. Follow the [build guide](docs/BUILDING.md). Keep platform-specific work on its target OS.
4. Explain what changed and how you checked it in the pull request. Mention anything you could not verify.

Product behavior is described in [`specs/`](specs/SPEC-MAP.md). [`BOOT.md`](specs/protocols/BOOT.md) is the short entrypoint to that workflow; [`AGENTS.md`](AGENTS.md) covers repository instructions. Small, one-session changes can stay small. Larger work gets a work item so its scope and unfinished steps remain visible.

Generated packages, downloaded models, local settings, and recordings belong outside Git. Preserve the existing recovery and data-retention behavior when changing recording or transcription.

## License

Contributions are distributed under the project's [MIT license](LICENSE). Keep upstream copyright and license notices with any third-party material you add. Keep discussion respectful and focused on the work.
