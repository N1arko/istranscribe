using System.Text.Json;
using Xunit;

namespace IsTranscribe.Platform.Windows.Tests;

/// <summary>
/// Guards the fail-closed Partner Center metadata and privacy handoff.
/// @spec spec://modules/platform/INFRA-005.B-store-signed-windows-distribution#submission
/// @spec spec://modules/app/FEAT-015-cloud-transcription-groq-openrouter#privacy
/// </summary>
public sealed class WindowsStoreSubmissionMetadataContractTests
{
    private static readonly string[] TopLevelFields =
    [
        "ageRatings",
        "configured",
        "fixture",
        "listings",
        "package",
        "pricingAvailability",
        "privacy",
        "product",
        "properties",
        "review",
        "schemaVersion",
        "submissionOptions",
        "systemRequirements"
    ];

    [Fact]
    public void CheckedInTemplateIsExactUnconfiguredAndBoundToPrivateManualFlight()
    {
        using var document = JsonDocument.Parse(ReadMetadata());
        var root = document.RootElement;

        Assert.Equal(
            TopLevelFields,
            root.EnumerateObject().Select(static property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("infra-005-store-submission-metadata-v1", RequiredString(root, "schemaVersion"));
        Assert.False(root.GetProperty("configured").GetBoolean());
        Assert.False(root.GetProperty("fixture").GetBoolean());

        var product = root.GetProperty("product");
        Assert.Equal("isTranscribe", RequiredString(product, "name"));
        AssertPlaceholder(RequiredString(product, "publisherDisplayName"));

        var package = root.GetProperty("package");
        AssertPlaceholder(RequiredString(package, "semanticVersion"));
        AssertPlaceholder(RequiredString(package, "sha256"));

        var availability = root.GetProperty("pricingAvailability");
        Assert.Equal("Free", RequiredString(availability, "basePrice"));
        Assert.Equal("all", RequiredString(availability, "markets"));
        Assert.Equal("private", RequiredString(availability, "audience"));
        Assert.Equal("direct-link", RequiredString(availability, "discoverability"));
        Assert.Equal("manual", RequiredString(availability, "publishingHold"));
    }

    [Fact]
    public void TemplateDeclaresOptInCloudTranscriptionPrivacyBoundaryAndIncompleteReview()
    {
        using var document = JsonDocument.Parse(ReadMetadata());
        var root = document.RootElement;

        var properties = root.GetProperty("properties");
        Assert.Equal("Productivity", RequiredString(properties, "category"));
        Assert.True(properties.GetProperty("personalInformationAccessed").GetBoolean());
        AssertPlaceholder(RequiredString(properties, "privacyPolicyUrl"));
        AssertPlaceholder(RequiredString(properties, "websiteUrl"));
        AssertPlaceholder(RequiredString(properties, "supportContact"));

        var privacy = root.GetProperty("privacy");
        Assert.True(privacy.GetProperty("microphoneAudio").GetBoolean());
        Assert.True(privacy.GetProperty("systemAudio").GetBoolean());
        Assert.True(privacy.GetProperty("localStorage").GetBoolean());
        Assert.True(privacy.GetProperty("transcriptionEnabled").GetBoolean());
        Assert.True(privacy.GetProperty("externalAudioTransfer").GetBoolean());
        Assert.False(privacy.GetProperty("telemetryEnabled").GetBoolean());
        Assert.NotEmpty(RequiredString(privacy, "retention"));
        Assert.NotEmpty(RequiredString(privacy, "deletion"));

        var ageRatings = root.GetProperty("ageRatings");
        Assert.False(ageRatings.GetProperty("questionnaireCompleted").GetBoolean());
        AssertPlaceholder(RequiredString(ageRatings, "reference"));

        var review = root.GetProperty("review");
        Assert.False(review.GetProperty("privacyPolicyReviewed").GetBoolean());
        Assert.False(review.GetProperty("metadataReviewed").GetBoolean());
        foreach (var field in new[] { "accountType", "partnerCenterProductId", "storeId", "reviewedBy", "reviewedUtc" })
        {
            AssertPlaceholder(RequiredString(review, field));
        }
    }

    [Fact]
    public void TemplateHasCompleteBoundedRussianAndEnglishListings()
    {
        using var document = JsonDocument.Parse(ReadMetadata());
        var listings = document.RootElement.GetProperty("listings").EnumerateArray().ToArray();

        Assert.Equal(2, listings.Length);
        Assert.Equal(new[] { "en-US", "ru-RU" }, listings.Select(listing => RequiredString(listing, "language")).Order());

        foreach (var listing in listings)
        {
            Assert.Equal("isTranscribe", RequiredString(listing, "productName"));

            var description = RequiredString(listing, "description");
            Assert.InRange(description.Length, 1, 10_000);
            Assert.Contains("MP3", description, StringComparison.Ordinal);
            Assert.Contains("Groq", description, StringComparison.Ordinal);
            Assert.Contains("OpenRouter", description, StringComparison.Ordinal);
            Assert.DoesNotContain("M4A", description, StringComparison.OrdinalIgnoreCase);

            var shortDescription = RequiredString(listing, "shortDescription");
            Assert.InRange(shortDescription.Length, 1, 1_000);
            Assert.Contains("MP3", shortDescription, StringComparison.Ordinal);

            var features = listing.GetProperty("features").EnumerateArray().Select(static value => value.GetString()!).ToArray();
            Assert.InRange(features.Length, 1, 20);
            Assert.All(features, feature => Assert.InRange(feature.Length, 1, 200));

            var keywords = listing.GetProperty("keywords").EnumerateArray().Select(static value => value.GetString()!).ToArray();
            Assert.InRange(keywords.Length, 1, 7);
            Assert.All(keywords, keyword => Assert.InRange(keyword.Length, 1, 40));
            Assert.InRange(keywords.Sum(CountWords), 1, 21);

            var screenshots = listing.GetProperty("screenshots").EnumerateArray().Select(static value => value.GetString()!).ToArray();
            Assert.Equal(4, screenshots.Length);
            Assert.All(screenshots, AssertPlaceholder);
            Assert.Equal(screenshots.Length, screenshots.Distinct(StringComparer.Ordinal).Count());
            var language = RequiredString(listing, "language");
            Assert.Equal(
                new[]
                {
                    $"{language}/ask.png",
                    $"{language}/ready.png",
                    $"{language}/recording.png",
                    $"{language}/suspected.png"
                },
                screenshots.Select(static value => value[1..^1]).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TemplateDocumentsRuntimeAndRestrictedCapabilityForCertification()
    {
        using var document = JsonDocument.Parse(ReadMetadata());
        var root = document.RootElement;

        var requirements = root.GetProperty("systemRequirements");
        Assert.Equal("win-x64", RequiredString(requirements, "runtimeIdentifier"));
        Assert.Equal("10.0.19041.0", RequiredString(requirements, "minimumWindowsVersion"));
        Assert.Equal("x64", RequiredString(requirements, "architecture"));

        var submission = root.GetProperty("submissionOptions");
        Assert.NotEmpty(RequiredString(submission, "notesForCertification"));
        var capabilities = submission.GetProperty("restrictedCapabilities").EnumerateArray().ToArray();
        var capability = Assert.Single(capabilities);
        Assert.Equal("runFullTrust", RequiredString(capability, "name"));
        Assert.Contains("audio", RequiredString(capability, "justification"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("consent", RequiredString(capability, "justification"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrivacyPolicyIsClearlyAReviewAndHostingTemplate()
    {
        var policy = File.ReadAllText(Path.Combine(ToolRoot(), "store", "privacy-policy.template.md"));

        Assert.Contains("TEMPLATE FOR REVIEW AND HOSTING", policy, StringComparison.Ordinal);
        Assert.Contains("<YYYY-MM-DD>", policy, StringComparison.Ordinal);
        Assert.Contains("<publisher name>", policy, StringComparison.Ordinal);
        Assert.Contains("<support email or HTTPS support URL>", policy, StringComparison.Ordinal);
        Assert.Contains("only after the user starts", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confirms a meeting recording prompt", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cloud transcription", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local MP3 files", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("M4A", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only after", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Groq", policy, StringComparison.Ordinal);
        Assert.Contains("OpenRouter", policy, StringComparison.Ordinal);
        Assert.Contains("does not include advertising or analytics telemetry", policy, StringComparison.Ordinal);
        Assert.Contains("legal/privacy review", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataVerifierIsReadOnlyFailClosedAndBoundToTheVerifiedStoreArtifact()
    {
        var verifier = File.ReadAllText(Path.Combine(ToolRoot(), "Test-WindowsStoreSubmissionMetadata.ps1"));

        foreach (var required in new[]
                 {
                     "$MetadataPath",
                     "$StoreReleaseDirectory",
                     "$AssetsRoot",
                     "$AllowTestFixture",
                     "infra-005-store-submission-metadata-v1",
                     "Test-WindowsStoreSubmissionArtifact.ps1",
                     "semanticVersion",
                     "sha256",
                     "configured",
                     "fixture",
                     "privacyPolicyReviewed",
                     "metadataReviewed",
                     "runFullTrust",
                     "suspected.png",
                     "recording.png",
                     "ready.png",
                     "ask.png",
                     "1366x768",
                     "50 MB",
                     "reparse",
                     "exactly eight unique localized screenshots"
                 })
        {
            Assert.Contains(required, verifier, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var forbiddenMutation in new[]
                 {
                     "Add-AppxPackage",
                     "Remove-AppxPackage",
                     "Import-Certificate",
                     "Import-PfxCertificate",
                     "New-SelfSignedCertificate",
                     "Set-ExecutionPolicy",
                     "Start-Process",
                     "Invoke-WebRequest",
                     "Invoke-RestMethod"
                 })
        {
            Assert.DoesNotContain(forbiddenMutation, verifier, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int CountWords(string value) => value
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Length;

    private static void AssertPlaceholder(string value)
    {
        Assert.StartsWith("<", value, StringComparison.Ordinal);
        Assert.EndsWith(">", value, StringComparison.Ordinal);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        return value.GetString()!;
    }

    private static string ReadMetadata() => File.ReadAllText(Path.Combine(
        ToolRoot(),
        "store-submission-metadata.template.json"));

    private static string ToolRoot() => Path.Combine(FindRepositoryRoot(), "packaging", "windows");

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

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
