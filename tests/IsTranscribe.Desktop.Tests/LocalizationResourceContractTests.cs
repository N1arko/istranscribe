using IsTranscribe.Application.Runtime;
using IsTranscribe.Desktop.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace IsTranscribe.Desktop.Tests;

/// <remarks>
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#scope.in
/// @spec spec://modules/app/FEAT-013-minimal-desktop-experience#verification
/// @spec spec://modules/app/FEAT-010.A-release-v2-localization#verification
/// </remarks>
public sealed partial class LocalizationResourceContractTests
{
    private static readonly HashSet<string> UserFacingAttributeNames = new(StringComparer.Ordinal)
    {
        "AutomationProperties.HelpText",
        "AutomationProperties.ItemStatus",
        "AutomationProperties.ItemType",
        "AutomationProperties.Name",
        "Content",
        "Description",
        "Header",
        "Text",
        "Title",
        "ToolTip",
        "ToolTip.Tip",
        "ToolTipText",
        "Watermark",
    };

    private static readonly HashSet<string> UserFacingElementNames = new(StringComparer.Ordinal)
    {
        "Button",
        "Label",
        "MenuItem",
        "NativeMenuItem",
        "Run",
        "TextBlock",
    };

    private static readonly HashSet<string> AllowedInvariantXamlLiterals = new(StringComparer.Ordinal)
    {
        "isTranscribe",
        "×",
        "⋯",
    };

    [Fact]
    public void Russian_and_english_resource_catalogs_have_identical_non_empty_keys_and_values()
    {
        var root = FindRepositoryRoot();
        var russian = ReadResources(ResourcePath(root, "ru"));
        var english = ReadResources(ResourcePath(root, "en"));

        Assert.NotEmpty(russian);
        Assert.NotEmpty(english);
        Assert.Equal(
            russian.Keys.Order(StringComparer.Ordinal),
            english.Keys.Order(StringComparer.Ordinal));
        Assert.All(russian, pair => AssertCatalogEntry("ru", pair));
        Assert.All(english, pair => AssertCatalogEntry("en", pair));
    }

    [Fact]
    public void Shared_capability_guidance_is_platform_neutral()
    {
        var root = FindRepositoryRoot();
        var russian = ReadResources(ResourcePath(root, "ru"));
        var english = ReadResources(ResourcePath(root, "en"));
        var sharedKeys = new[]
        {
            "String.Attention.Capability",
            "String.Setup.Capability.MicrophoneCaptureUnavailable"
        };

        foreach (var key in sharedKeys)
        {
            Assert.DoesNotContain("Windows", russian[key], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Windows", english[key], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void English_base_catalog_precedes_the_selected_language_override()
    {
        Assert.Equal(
            ["en", "ru"],
            LocalizationService.CatalogLanguageCodes(UiLanguage.Russian));
        Assert.Equal(
            ["en"],
            LocalizationService.CatalogLanguageCodes(UiLanguage.English));
    }

    [Fact]
    public void Every_release_xaml_string_reference_exists_in_both_languages()
    {
        var root = FindRepositoryRoot();
        var desktopRoot = Path.Combine(root, "src", "IsTranscribe.Desktop");
        var russian = ReadResources(ResourcePath(root, "ru"));
        var english = ReadResources(ResourcePath(root, "en"));
        var references = EnumerateReleaseFiles(desktopRoot, "*.axaml")
            .SelectMany(path => StringResourceReferenceRegex()
                .Matches(File.ReadAllText(path))
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(references);
        Assert.All(references, key => Assert.True(russian.ContainsKey(key), $"Missing ru resource: {key}"));
        Assert.All(references, key => Assert.True(english.ContainsKey(key), $"Missing en resource: {key}"));
    }

    [Fact]
    public void Every_release_csharp_string_reference_resolves_in_both_catalogs()
    {
        var root = FindRepositoryRoot();
        var desktopRoot = Path.Combine(root, "src", "IsTranscribe.Desktop");
        var russian = ReadResources(ResourcePath(root, "ru"));
        var english = ReadResources(ResourcePath(root, "en"));
        var sources = EnumerateReleaseFiles(desktopRoot, "*.cs")
            .Select(File.ReadAllText)
            .ToArray();
        var staticReferences = sources
            .SelectMany(source => CSharpStaticStringResourceReferenceRegex()
                .Matches(source)
                .Select(match => match.Groups[1].Value));
        var dynamicTemplates = sources
            .SelectMany(source => CSharpInterpolatedStringResourceReferenceRegex()
                .Matches(source)
                .Select(match => match.Groups[1].Value));
        var references = staticReferences
            .Concat(dynamicTemplates.SelectMany(template => ResolveDynamicReferences(template, sources)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(references);
        Assert.All(references, key => Assert.True(russian.ContainsKey(key), $"Missing ru resource: {key}"));
        Assert.All(references, key => Assert.True(english.ContainsKey(key), $"Missing en resource: {key}"));
    }

    [Fact]
    public void Russian_and_english_placeholders_have_identical_indexes_and_format_clauses()
    {
        var root = FindRepositoryRoot();
        var russian = ReadResources(ResourcePath(root, "ru"));
        var english = ReadResources(ResourcePath(root, "en"));

        foreach (var key in english.Keys.Order(StringComparer.Ordinal))
        {
            Assert.Equal(
                PlaceholderContract(english[key]),
                PlaceholderContract(russian[key]));
            AssertValidCompositeFormat("en", key, english[key]);
            AssertValidCompositeFormat("ru", key, russian[key]);
        }
    }

    [Fact]
    public void Every_release_xaml_user_facing_literal_is_resource_backed_or_allowlisted()
    {
        var root = FindRepositoryRoot();
        var desktopRoot = Path.Combine(root, "src", "IsTranscribe.Desktop");
        var offenders = EnumerateReleaseFiles(desktopRoot, "*.axaml")
            .Where(path => !IsLocalizationCatalog(path))
            .SelectMany(path => FindHardcodedXamlCopy(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Release XAML contains user-facing literals outside the invariant brand/glyph/data allowlist:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void Main_window_polish_keeps_overflow_primary_action_and_copy_hierarchy_coherent()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Desktop",
            "Views",
            "MainWindow.axaml"));

        Assert.Contains("Margin=\"14,0,0,0\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Center\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("VerticalContentAlignment=\"Center\"", mainWindow, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(mainWindow, "<Ellipse Width=\"3\" Height=\"3\"").Count);
        Assert.DoesNotContain("•••", mainWindow, StringComparison.Ordinal);
        Assert.Contains(
            "Button.primary:pointerover /template/ ContentPresenter#PART_ContentPresenter",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains("Brush.Accent.Hover", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Brush.Accent.Pressed", mainWindow, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding ShowStateKicker}\"", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("String.Privacy.LocalAudio", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void Browser_fallback_scope_is_explained_only_below_its_checkbox_in_both_languages()
    {
        var root = FindRepositoryRoot();
        var setupWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Desktop",
            "Views",
            "SetupWindow.axaml"));
        var settingsWindow = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Desktop",
            "Views",
            "SettingsWindow.axaml"));
        var russian = ReadResources(ResourcePath(root, "ru"));
        var english = ReadResources(ResourcePath(root, "en"));

        Assert.Contains(
            "{DynamicResource String.Settings.Applications.Description}",
            settingsWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "{DynamicResource String.App.OtherBrowser.Description}",
            setupWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "{DynamicResource String.App.OtherBrowser.Description}",
            settingsWindow,
            StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsBrowserFallback}\"", setupWindow, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsBrowserFallback}\"", settingsWindow, StringComparison.Ordinal);

        Assert.Equal("Другие сервисы в браузере", russian["String.App.OtherBrowser"]);
        Assert.Equal("Other browser services", english["String.App.OtherBrowser"]);
        Assert.Equal(
            "Для сервисов, которых нет выше. Работает в Chrome, Edge, Firefox, Zen и других поддерживаемых браузерах.",
            russian["String.App.OtherBrowser.Description"]);
        Assert.Equal(
            "For services not listed above. Works in Chrome, Edge, Firefox, Zen, and other supported browsers.",
            english["String.App.OtherBrowser.Description"]);

        Assert.Contains("isTranscribe", russian["String.Settings.Applications.Description"], StringComparison.Ordinal);
        Assert.Contains("Снимите флажок", russian["String.Settings.Applications.Description"], StringComparison.Ordinal);
        Assert.DoesNotContain("Zen", russian["String.Settings.Applications.Description"], StringComparison.Ordinal);
        Assert.DoesNotContain("Zen", russian["String.Setup.Apps.Description"], StringComparison.Ordinal);
        Assert.Contains("isTranscribe", english["String.Settings.Applications.Description"], StringComparison.Ordinal);
        Assert.Contains("Clear a checkbox", english["String.Settings.Applications.Description"], StringComparison.Ordinal);
        Assert.DoesNotContain("Zen", english["String.Settings.Applications.Description"], StringComparison.Ordinal);
        Assert.DoesNotContain("Zen", english["String.Setup.Apps.Description"], StringComparison.Ordinal);
    }

    [Fact]
    public void Release_csharp_presentation_assignments_do_not_embed_user_facing_copy()
    {
        var root = FindRepositoryRoot();
        var desktopRoot = Path.Combine(root, "src", "IsTranscribe.Desktop");
        var offenders = EnumerateReleaseFiles(desktopRoot, "*.cs")
            .SelectMany(path => CSharpUserFacingLiteralAssignmentRegex()
                .Matches(File.ReadAllText(path))
                .Select(match => $"{Path.GetRelativePath(root, path)}: {match.Value.Trim()}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Release C# presentation state contains hardcoded user-facing copy:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }

    [Fact]
    public void Release_tray_copy_is_resource_backed_and_refreshes_on_language_change()
    {
        var root = FindRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(root, "src", "IsTranscribe.Desktop", "App.axaml"));
        var appCode = File.ReadAllText(Path.Combine(root, "src", "IsTranscribe.Desktop", "App.axaml.cs"));

        Assert.All(
            new[]
            {
                "String.Service.Paused",
                "String.Action.RecordNow",
                "String.Action.Pause",
                "String.Action.Finish",
                "String.Action.OpenApp",
                "String.Action.Settings",
                "String.Action.Quit"
            },
            key => Assert.Contains($"{{DynamicResource {key}}}", appXaml, StringComparison.Ordinal));
        Assert.Contains("String.Tray.Tooltip.Format", appCode, StringComparison.Ordinal);
        Assert.Matches(TrayLanguageRefreshRegex(), appCode);
    }

    [Fact]
    // @spec spec://modules/app/FEAT-014-transcription-extension-seam#verification
    // @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#verification
    // @spec spec://modules/app/FEAT-016-local-whisper-transcription#experience
    public void Release_views_expose_remote_and_local_transcription_without_obsolete_fireworks_controls()
    {
        var root = FindRepositoryRoot();
        var russian = ReadResources(ResourcePath(root, "ru"));
        var english = ReadResources(ResourcePath(root, "en"));
        var allCopy = string.Join('\n', russian.Values.Concat(english.Values));
        var views = Directory
            .EnumerateFiles(
                Path.Combine(root, "src", "IsTranscribe.Desktop", "Views"),
                "*.axaml",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.DoesNotMatch(LegacyCopyRegex(), allCopy);
        Assert.All(views, xaml => Assert.DoesNotContain("Fireworks", xaml, StringComparison.OrdinalIgnoreCase));
        var settings = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Desktop",
            "Views",
            "SettingsWindow.axaml"));
        var main = File.ReadAllText(Path.Combine(
            root,
            "src",
            "IsTranscribe.Desktop",
            "Views",
            "MainWindow.axaml"));
        Assert.Contains("PasswordChar=\"●\"", settings, StringComparison.Ordinal);
        Assert.Contains("SaveTranscriptionCredentialCommand", settings, StringComparison.Ordinal);
        Assert.Contains("DeleteTranscriptionCredentialCommand", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("DiscoverTranscriptionModelsCommand", settings, StringComparison.Ordinal);
        Assert.Contains("TranscriptionConsentAccepted", settings, StringComparison.Ordinal);
        Assert.Contains("ShowZeroDataRetentionOption", settings, StringComparison.Ordinal);
        Assert.Contains("RequireZeroDataRetention", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomaticTranscriptionEnabled", settings, StringComparison.Ordinal);
        Assert.Contains("TranscriptionModeIndex", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedTranscriptionModel}", settings, StringComparison.Ordinal);
        Assert.Contains("ShowRemoteTranscriptionSettings", settings, StringComparison.Ordinal);
        Assert.Contains("ShowLocalTranscriptionSettings", settings, StringComparison.Ordinal);
        Assert.Contains("InstallLocalModelCommand", settings, StringComparison.Ordinal);
        Assert.Contains("CancelLocalModelInstallCommand", settings, StringComparison.Ordinal);
        Assert.Contains("RemoveLocalModelCommand", settings, StringComparison.Ordinal);
        Assert.Contains("ShowLocalTranscriptionRecordingNotice", settings, StringComparison.Ordinal);
        Assert.Contains("ShowLocalResourceWarning", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("CanEnableAutomaticTranscription", settings, StringComparison.Ordinal);
        Assert.Equal("142 МБ", russian["String.Transcription.LocalModel.Size.Base"]);
        Assert.Equal("466 МБ", russian["String.Transcription.LocalModel.Size.Small"]);
        Assert.Equal("1,5 ГБ", russian["String.Transcription.LocalModel.Size.Medium"]);
        Assert.Equal("142 MB", english["String.Transcription.LocalModel.Size.Base"]);
        Assert.Equal("466 MB", english["String.Transcription.LocalModel.Size.Small"]);
        Assert.Equal("1.5 GB", english["String.Transcription.LocalModel.Size.Medium"]);
        Assert.Contains("RequestTranscriptionCommand", main, StringComparison.Ordinal);
        Assert.Contains("CancelTranscriptionCommand", main, StringComparison.Ordinal);
        Assert.Contains("RetryTranscriptionCommand", main, StringComparison.Ordinal);
        Assert.Contains("ContinueLocalTranscriptionCommand", main, StringComparison.Ordinal);
        Assert.All(
            views,
            xaml => Assert.All(
                new[]
                {
                    "MinSpeakers",
                    "MaxSpeakers",
                    "TranscriptionProvider",
                    "ProviderEndpoint"
                },
                token => Assert.DoesNotContain(token, xaml, StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyDictionary<string, string> ReadResources(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument
            .Load(path)
            .Descendants()
            .Where(element => element.Name.LocalName == "String")
            .ToDictionary(
                element => (string?)element.Attribute(x + "Key")
                    ?? throw new InvalidDataException($"String without x:Key in {path}."),
                element => element.Value,
                StringComparer.Ordinal);
    }

    private static void AssertCatalogEntry(string language, KeyValuePair<string, string> pair)
    {
        Assert.Matches(ResourceKeyRegex(), pair.Key);
        Assert.False(
            string.IsNullOrWhiteSpace(pair.Value),
            $"Localized resource '{pair.Key}' is empty in the {language} catalog.");
    }

    private static IReadOnlyList<string> ResolveDynamicReferences(
        string template,
        IReadOnlyCollection<string> sources)
    {
        var normalized = InterpolationHoleRegex().Replace(template, "{}");
        return normalized switch
        {
            "String.State.{}.Title" => Enum.GetNames<ApplicationActivityState>()
                .Select(state => $"String.State.{state}.Title")
                .ToArray(),
            "String.State.{}.Description" => Enum.GetNames<ApplicationActivityState>()
                .Select(state => $"String.State.{state}.Description")
                .ToArray(),
            "String.Diagnostics.Field.{}" => sources
                .SelectMany(source => DiagnosticRowSuffixRegex()
                    .Matches(source)
                    .Select(match => $"String.Diagnostics.Field.{match.Groups[1].Value}"))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            _ => FailUnknownDynamicTemplate(template),
        };
    }

    private static IReadOnlyList<string> FailUnknownDynamicTemplate(string template)
    {
        Assert.Fail($"Add deterministic resource resolution for interpolated C# key template '{template}'.");
        return [];
    }

    private static string[] PlaceholderContract(string value) => CompositeFormatPlaceholderRegex()
        .Matches(value)
        .Select(match => string.Concat(
            match.Groups["index"].Value,
            match.Groups["alignment"].Value,
            match.Groups["format"].Value))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static void AssertValidCompositeFormat(string language, string key, string value)
    {
        var indexes = CompositeFormatPlaceholderRegex()
            .Matches(value)
            .Select(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture))
            .ToArray();
        var arguments = new object?[indexes.Length == 0 ? 0 : indexes.Max() + 1];
        var exception = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, value, arguments));

        Assert.True(
            exception is null,
            $"Localized resource '{key}' has an invalid composite format in the {language} catalog: {exception?.Message}");
    }

    private static IEnumerable<string> FindHardcodedXamlCopy(string root, string path)
    {
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes()
                         .Where(attribute => UserFacingAttributeNames.Contains(attribute.Name.LocalName)))
            {
                var value = attribute.Value.Trim();
                if (IsResourceOrDataReference(value) || AllowedInvariantXamlLiterals.Contains(value))
                {
                    continue;
                }

                yield return FormatXamlOffender(root, path, attribute, attribute.Name.LocalName, value);
            }

            if (!UserFacingElementNames.Contains(element.Name.LocalName))
            {
                continue;
            }

            foreach (var text in element.Nodes().OfType<XText>())
            {
                var value = text.Value.Trim();
                if (value.Length == 0
                    || IsResourceOrDataReference(value)
                    || AllowedInvariantXamlLiterals.Contains(value))
                {
                    continue;
                }

                yield return FormatXamlOffender(root, path, text, element.Name.LocalName, value);
            }
        }
    }

    private static bool IsResourceOrDataReference(string value) =>
        value.Length == 0 || XamlMarkupReferenceRegex().IsMatch(value);

    private static string FormatXamlOffender(
        string root,
        string path,
        XObject node,
        string property,
        string value)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var lineNumber = node is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
            ? lineInfo.LineNumber
            : 0;
        return $"{relativePath}:{lineNumber} {property}=\"{value}\"";
    }

    private static IEnumerable<string> EnumerateReleaseFiles(string desktopRoot, string pattern) => Directory
        .EnumerateFiles(desktopRoot, pattern, SearchOption.AllDirectories)
        .Where(path => !IsBuildOutput(path))
        .Order(StringComparer.Ordinal);

    private static bool IsBuildOutput(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalizationCatalog(string path) => path.Contains(
        $"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}Resources{Path.DirectorySeparatorChar}Strings.",
        StringComparison.OrdinalIgnoreCase);

    private static string ResourcePath(string root, string language) => Path.Combine(
        root,
        "src",
        "IsTranscribe.Desktop",
        "Localization",
        "Resources",
        $"Strings.{language}.axaml");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "specs", "BOARD.md")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root from the test output path.");
    }

    [GeneratedRegex(@"(?:DynamicResource|StaticResource)\s+(String\.[A-Za-z0-9.]+)")]
    private static partial Regex StringResourceReferenceRegex();

    [GeneratedRegex("^String\\.(?:[A-Za-z0-9]+\\.)*[A-Za-z0-9]+$")]
    private static partial Regex ResourceKeyRegex();

    [GeneratedRegex("(?<!\\$)\"(String\\.[A-Za-z0-9.]+)\"")]
    private static partial Regex CSharpStaticStringResourceReferenceRegex();

    [GeneratedRegex("\\$\"(String\\.[^\"\\r\\n]*\\{[^\"\\r\\n]+)\"")]
    private static partial Regex CSharpInterpolatedStringResourceReferenceRegex();

    [GeneratedRegex(@"\{[^{}]+\}")]
    private static partial Regex InterpolationHoleRegex();

    [GeneratedRegex("\\bRow\\(\\s*\"([A-Za-z0-9]+)\"")]
    private static partial Regex DiagnosticRowSuffixRegex();

    [GeneratedRegex(
        "(?:AttentionMessage|Body|CancelAction|Description|DestructiveAction|DismissActionText|ErrorMessage|Header|OpenActionText|PrimaryAction|StateDescription|StateKicker|StateTitle|StatusMessage|Title|ToolTipText)\\s*=\\s*\\$?\"(?!String\\.)[^\"\\r\\n]+\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex CSharpUserFacingLiteralAssignmentRegex();

    [GeneratedRegex(
        "private void Localization_OnLanguageChanged\\([^)]*\\)\\s*\\{(?:(?!\\n    \\}).)*UpdateTrayPresentation\\(_runtime\\.Snapshot\\);(?:(?!\\n    \\}).)*\\n    \\}",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex TrayLanguageRefreshRegex();

    [GeneratedRegex(
        @"(?<!\{)\{(?<index>[0-9]+)(?<alignment>,-?[0-9]+)?(?<format>:[^{}]+)?\}(?!\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompositeFormatPlaceholderRegex();

    [GeneratedRegex(
        @"^\{(?:Binding|CompiledBinding|DynamicResource|ReflectionBinding|StaticResource|TemplateBinding|x:Null)(?:\s|,|\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex XamlMarkupReferenceRegex();

    [GeneratedRegex(
        @"fireworks|provider\s*endpoint",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyCopyRegex();
}
