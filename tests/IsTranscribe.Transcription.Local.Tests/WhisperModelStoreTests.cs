using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using IsTranscribe.Transcription.Local.Models;
using Xunit;

namespace IsTranscribe.Transcription.Local.Tests;

/// <summary>
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#model-download
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#models
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#artifacts
/// @spec spec://modules/app/FEAT-016-local-whisper-transcription#privacy
/// </summary>
public sealed class WhisperModelStoreTests
{
    [Fact]
    public void ConstructorRequiresFullLoadAndDurableRetentionSeams()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new ScriptedHandler((_, _, _) =>
            throw new InvalidOperationException("Construction must remain offline.")));
        var activation = new DeterministicFullLoadVerifier();
        var retention = new TestRetentionLeaseProvider();

        Assert.Throws<ArgumentNullException>(() => new WhisperModelStore(
            directory.Path,
            client,
            null!,
            retention));
        Assert.Throws<ArgumentNullException>(() => new WhisperModelStore(
            directory.Path,
            client,
            activation,
            null!));
    }

    [Fact]
    public async Task InstalledPayloadIsReusedOfflineWithoutASecondRequest()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(73, seed: 11);
        var descriptor = CreateDescriptor("fixture", payload);
        var handler = new ScriptedHandler((_, requestNumber, _) => requestNumber == 1
            ? Response(HttpStatusCode.OK, payload)
            : throw new InvalidOperationException("Offline reuse attempted network access."));
        using var client = new HttpClient(handler);
        await using var store = CreateStore(directory.Path, client);

        var first = await store.AcquireAsync(descriptor);
        var second = await store.AcquireAsync(descriptor);

        Assert.Equal(ModelAcquisitionDisposition.Downloaded, first.Disposition);
        Assert.Equal(ModelAcquisitionDisposition.ReusedInstalledPayload, second.Disposition);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, second.BytesReceivedFromNetwork);
        Assert.True(File.Exists(first.Installation.PayloadPath));
        Assert.Equal(descriptor.Sha256, await HashFileAsync(first.Installation.PayloadPath));
    }

    [Fact]
    public async Task ExplicitRetryRepairsCorruptInstalledPayloadFromFreshDownload()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(79, seed: 12);
        var descriptor = CreateDescriptor("repair", payload);
        var handler = new ScriptedHandler((_, _, _) => Response(HttpStatusCode.OK, payload));
        using var client = new HttpClient(handler);
        await using var store = CreateStore(directory.Path, client);
        var initial = await store.AcquireAsync(descriptor);
        var corrupted = payload.ToArray();
        corrupted[^1] ^= 0xff;
        await File.WriteAllBytesAsync(initial.Installation.PayloadPath, corrupted);

        Assert.Null(await store.GetActiveInstallationAsync(descriptor));
        var repaired = await store.AcquireAsync(descriptor);

        Assert.Equal(ModelAcquisitionDisposition.Downloaded, repaired.Disposition);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(descriptor.Sha256, await HashFileAsync(repaired.Installation.PayloadPath));
        Assert.NotNull(await store.GetActiveInstallationAsync(descriptor));
    }

    [Fact]
    public async Task PartialDownloadResumesWithValidated206Range()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(91, seed: 22);
        var descriptor = CreateDescriptor("resume", payload);
        const int prefixLength = 29;

        using (var seedClient = new HttpClient(new ScriptedHandler((_, _, _) =>
                   Response(HttpStatusCode.OK, payload[..prefixLength]))))
        await using (var seedStore = CreateStore(directory.Path, seedClient, maximumAttempts: 1))
        {
            var incomplete = await Assert.ThrowsAsync<ModelStoreException>(() => seedStore.AcquireAsync(descriptor));
            Assert.Equal(ModelStoreError.DownloadIncomplete, incomplete.Error);
        }

        var resumeHandler = new ScriptedHandler((request, _, _) =>
        {
            Assert.Equal($"bytes={prefixLength}-", request.Headers.Range?.ToString());
            var response = Response(HttpStatusCode.PartialContent, payload[prefixLength..]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefixLength,
                payload.Length - 1,
                payload.Length);
            return response;
        });
        using var resumeClient = new HttpClient(resumeHandler);
        await using var store = CreateStore(directory.Path, resumeClient);

        var result = await store.AcquireAsync(descriptor);

        Assert.Equal(ModelAcquisitionDisposition.Resumed, result.Disposition);
        Assert.Equal(payload.Length - prefixLength, result.BytesReceivedFromNetwork);
        Assert.Equal(descriptor.Sha256, await HashFileAsync(result.Installation.PayloadPath));
    }

    [Fact]
    public async Task Full200ResponseRestartsAnExistingRangeSafely()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(83, seed: 33);
        var descriptor = CreateDescriptor("restart", payload);
        const int prefixLength = 17;

        await SeedPartialAsync(directory.Path, descriptor, payload[..prefixLength]);

        var handler = new ScriptedHandler((request, _, _) =>
        {
            Assert.Equal($"bytes={prefixLength}-", request.Headers.Range?.ToString());
            return Response(HttpStatusCode.OK, payload);
        });
        using var client = new HttpClient(handler);
        await using var store = CreateStore(directory.Path, client);

        var result = await store.AcquireAsync(descriptor);

        Assert.Equal(ModelAcquisitionDisposition.DownloadedAfterServerRestart, result.Disposition);
        Assert.Equal(payload.Length, result.BytesReceivedFromNetwork);
        Assert.Equal(descriptor.Sha256, await HashFileAsync(result.Installation.PayloadPath));
    }

    [Fact]
    public async Task ExactLengthPartialPromotesAfter416WithoutRedownload()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(67, seed: 44);
        var descriptor = CreateDescriptor("range-complete", payload);
        var gatedStream = new FullThenWaitStream(payload);
        var progress = new CompletionProgress(payload.Length);
        using var cancelClient = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, new StreamContent(gatedStream))));
        await using (var cancelStore = CreateStore(directory.Path, cancelClient))
        using (var cancellation = new CancellationTokenSource())
        {
            var paths = cancelStore.GetPaths(descriptor);
            var acquisition = cancelStore.AcquireAsync(descriptor, progress, cancellation.Token);
            await progress.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);

            Assert.Equal(payload.Length, new FileInfo(paths.PartialPath).Length);
            Assert.True(File.Exists(paths.PartialStatePath));
        }

        var rangeHandler = new ScriptedHandler((request, _, _) =>
        {
            Assert.Equal($"bytes={payload.Length}-", request.Headers.Range?.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content = new ByteArrayContent([]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(payload.Length);
            return response;
        });
        using var rangeClient = new HttpClient(rangeHandler);
        await using var store = CreateStore(directory.Path, rangeClient);

        var result = await store.AcquireAsync(descriptor);

        Assert.Equal(ModelAcquisitionDisposition.Resumed, result.Disposition);
        Assert.Equal(0, result.BytesReceivedFromNetwork);
        Assert.Equal(descriptor.Sha256, await HashFileAsync(result.Installation.PayloadPath));
    }

    [Fact]
    public async Task CancellationPreservesPartialAndSidecarForResume()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(79, seed: 55);
        var descriptor = CreateDescriptor("cancel", payload);
        var prefix = payload[..31];
        var gatedStream = new FullThenWaitStream(prefix);
        var progress = new CompletionProgress(prefix.Length);
        using var client = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, new StreamContent(gatedStream))));
        await using var store = CreateStore(directory.Path, client);
        using var cancellation = new CancellationTokenSource();

        var paths = store.GetPaths(descriptor);
        var acquisition = store.AcquireAsync(descriptor, progress, cancellation.Token);
        await progress.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);

        Assert.True(File.Exists(paths.PartialPath));
        Assert.Equal(prefix.Length, new FileInfo(paths.PartialPath).Length);
        Assert.True(File.Exists(paths.PartialStatePath));
        Assert.False(File.Exists(paths.PayloadPath));
    }

    [Fact]
    public async Task FullAttemptTimeoutRetriesHungBodyFromPreservedPartial()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(87, seed: 56);
        var descriptor = CreateDescriptor("attempt-timeout", payload);
        const int prefixLength = 27;
        var hangingBody = new FullThenIgnoreCancellationStream(payload[..prefixLength]);
        var handler = new ScriptedHandler((request, requestNumber, _) =>
        {
            if (requestNumber == 1)
            {
                Assert.Null(request.Headers.Range);
                return Response(HttpStatusCode.OK, new StreamContent(hangingBody));
            }

            Assert.Equal($"bytes={prefixLength}-", request.Headers.Range?.ToString());
            var response = Response(HttpStatusCode.PartialContent, payload[prefixLength..]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefixLength,
                payload.Length - 1,
                payload.Length);
            return response;
        });
        using var client = new HttpClient(handler);
        await using var store = CreateStore(
            directory.Path,
            client,
            maximumAttempts: 2,
            downloadAttemptTimeout: TimeSpan.FromMilliseconds(150));

        var result = await store.AcquireAsync(descriptor);

        Assert.True(hangingBody.PayloadWasRead.Task.IsCompleted);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(ModelAcquisitionDisposition.Resumed, result.Disposition);
        Assert.Equal(descriptor.Sha256, await HashFileAsync(result.Installation.PayloadPath));
        var paths = store.GetPaths(descriptor);
        Assert.False(File.Exists(paths.PartialPath));
        Assert.False(File.Exists(paths.PartialStatePath));
    }

    [Fact]
    public async Task BadHashDeletesOnlyInvalidPartialAndKeepsWorkingReplacement()
    {
        using var directory = new TemporaryDirectory();
        var oldPayload = CreatePayload(63, seed: 66);
        var newPayload = CreatePayload(71, seed: 77);
        var oldDescriptor = CreateDescriptor("replace", oldPayload, catalogVersion: "1");
        var newDescriptor = CreateDescriptor("replace", newPayload, catalogVersion: "2");

        using (var oldClient = new HttpClient(new ScriptedHandler((_, _, _) =>
                   Response(HttpStatusCode.OK, oldPayload))))
        await using (var oldStore = CreateStore(directory.Path, oldClient))
        {
            _ = await oldStore.AcquireAsync(oldDescriptor);
        }

        var invalidPayload = (byte[])newPayload.Clone();
        invalidPayload[^1] ^= 0x5a;
        using (var invalidClient = new HttpClient(new ScriptedHandler((_, _, _) =>
                   Response(HttpStatusCode.OK, invalidPayload))))
        await using (var invalidStore = CreateStore(directory.Path, invalidClient))
        {
            var failure = await Assert.ThrowsAsync<ModelStoreException>(() =>
                invalidStore.AcquireAsync(newDescriptor));
            Assert.Equal(ModelStoreError.PayloadHashMismatch, failure.Error);

            var newPaths = invalidStore.GetPaths(newDescriptor);
            Assert.False(File.Exists(newPaths.PartialPath));
            Assert.False(File.Exists(newPaths.PartialStatePath));
            Assert.False(File.Exists(newPaths.PayloadPath));

            var oldInstallation = await invalidStore.GetActiveInstallationAsync(oldDescriptor);
            Assert.NotNull(oldInstallation);
            Assert.True(File.Exists(oldInstallation.PayloadPath));
        }

        using var replacementClient = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, newPayload)));
        await using var replacementStore = CreateStore(directory.Path, replacementClient);
        var oldPath = replacementStore.GetPaths(oldDescriptor).PayloadPath;

        var replacement = await replacementStore.AcquireAsync(newDescriptor);

        Assert.Equal(newDescriptor.Sha256, replacement.Installation.Sha256);
        Assert.True(File.Exists(oldPath));
        var cleanup = await replacementStore.RecoverRemovalsAndCollectOrphansAsync();
        Assert.Contains(oldDescriptor.Sha256, cleanup.DeletedOrphanSha256);
        Assert.False(File.Exists(oldPath));
        Assert.NotNull(await replacementStore.GetActiveInstallationAsync(newDescriptor));

        var removal = await replacementStore.RemoveActiveAsync(newDescriptor);
        Assert.True(removal.Removed);
        Assert.True(removal.PayloadDeleted);
        Assert.False(removal.PayloadRetainedByLease);
        Assert.False(File.Exists(replacement.Installation.PayloadPath));
        Assert.Null(await replacementStore.GetActiveInstallationAsync(newDescriptor));
    }

    [Fact]
    public async Task ReplacementRetainsDurableJobBlobUntilLeaseIsReleasedAndCollected()
    {
        using var directory = new TemporaryDirectory();
        var oldPayload = CreatePayload(75, seed: 78);
        var newPayload = CreatePayload(81, seed: 79);
        var oldDescriptor = CreateDescriptor("leased-replacement", oldPayload, catalogVersion: "1");
        var newDescriptor = CreateDescriptor("leased-replacement", newPayload, catalogVersion: "2");
        var retention = new TestRetentionLeaseProvider();

        using (var oldClient = new HttpClient(new ScriptedHandler((_, _, _) =>
                   Response(HttpStatusCode.OK, oldPayload))))
        await using (var oldStore = CreateStore(
                         directory.Path,
                         oldClient,
                         retentionLeaseProvider: retention))
        {
            _ = await oldStore.AcquireAsync(oldDescriptor);
        }

        retention.Retain(oldDescriptor.Sha256);
        using var newClient = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, newPayload)));
        await using var store = CreateStore(
            directory.Path,
            newClient,
            retentionLeaseProvider: retention);
        var oldPath = store.GetPaths(oldDescriptor).PayloadPath;

        _ = await store.AcquireAsync(newDescriptor);

        Assert.True(File.Exists(oldPath));
        var retainedCleanup = await store.RecoverRemovalsAndCollectOrphansAsync();
        Assert.Contains(oldDescriptor.Sha256, retainedCleanup.RetainedOrphanSha256);
        Assert.True(File.Exists(oldPath));

        retention.Release(oldDescriptor.Sha256);
        var releasedCleanup = await store.RecoverRemovalsAndCollectOrphansAsync();
        Assert.Contains(oldDescriptor.Sha256, releasedCleanup.DeletedOrphanSha256);
        Assert.False(File.Exists(oldPath));
        Assert.NotNull(await store.GetActiveInstallationAsync(newDescriptor));
    }

    [Fact]
    public async Task OrphanCleanupWaitsForRetentionBeforeTakingStoreMutationGate()
    {
        using var directory = new TemporaryDirectory();
        var oldPayload = CreatePayload(79, seed: 790);
        var newPayload = CreatePayload(83, seed: 791);
        var oldDescriptor = CreateDescriptor("lease-order", oldPayload, catalogVersion: "1");
        var newDescriptor = CreateDescriptor("lease-order", newPayload, catalogVersion: "2");
        var retention = new OrderingRetentionLeaseProvider();
        using (var oldClient = new HttpClient(new ScriptedHandler((_, _, _) =>
                   Response(HttpStatusCode.OK, oldPayload))))
        await using (var oldStore = CreateStore(
                         directory.Path,
                         oldClient,
                         retentionLeaseProvider: retention))
        {
            _ = await oldStore.AcquireAsync(oldDescriptor);
        }

        using var newClient = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, newPayload)));
        await using var store = CreateStore(
            directory.Path,
            newClient,
            retentionLeaseProvider: retention);
        _ = await store.AcquireAsync(newDescriptor);
        var usageLease = await retention.AcquireUsageLeaseAsync(
            oldDescriptor.Sha256,
            CancellationToken.None);

        var cleanup = store.RecoverRemovalsAndCollectOrphansAsync();
        await retention.DeletionLeaseRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var active = await store.GetActiveInstallationAsync(newDescriptor)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(active);
        }
        finally
        {
            await usageLease.DisposeAsync();
        }

        var result = await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(oldDescriptor.Sha256, result.DeletedOrphanSha256);
    }

    [Fact]
    public async Task RemovalRetainsDurableJobBlobUntilLeaseIsReleasedAndCollected()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(69, seed: 80);
        var descriptor = CreateDescriptor("leased-removal", payload);
        var retention = new TestRetentionLeaseProvider();
        using var client = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, payload)));
        await using var store = CreateStore(
            directory.Path,
            client,
            retentionLeaseProvider: retention);
        var installation = await store.AcquireAsync(descriptor);
        retention.Retain(descriptor.Sha256);

        var removal = await store.RemoveActiveAsync(descriptor);

        Assert.True(removal.Removed);
        Assert.False(removal.PayloadDeleted);
        Assert.True(removal.PayloadRetainedByLease);
        Assert.True(File.Exists(installation.Installation.PayloadPath));
        Assert.Null(await store.GetActiveInstallationAsync(descriptor));

        retention.Release(descriptor.Sha256);
        var cleanup = await store.RecoverRemovalsAndCollectOrphansAsync();
        Assert.Contains(descriptor.Sha256, cleanup.DeletedOrphanSha256);
        Assert.False(File.Exists(installation.Installation.PayloadPath));
    }

    [Fact]
    public async Task UnknownRetentionStateRestoresActivePointerAndKeepsPayload()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(73, seed: 82);
        var descriptor = CreateDescriptor("retention-unavailable", payload);
        using var client = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, payload)));
        await using var store = CreateStore(
            directory.Path,
            client,
            retentionLeaseProvider: new FailingRetentionLeaseProvider());
        var installation = await store.AcquireAsync(descriptor);

        var failure = await Assert.ThrowsAsync<ModelStoreException>(() => store.RemoveActiveAsync(descriptor));

        Assert.Equal(ModelStoreError.RetentionStateUnavailable, failure.Error);
        Assert.True(File.Exists(installation.Installation.PayloadPath));
        Assert.NotNull(await store.GetActiveInstallationAsync(descriptor));
    }

    [Fact]
    public async Task TombstoneCleanupFailureNeverRestoresPointerAfterPayloadDeletion()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(77, seed: 81);
        var descriptor = CreateDescriptor("remove-commit", payload);
        var mutator = new TombstoneDeleteFaultMutator();
        using var client = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, payload)));
        await using var store = CreateStore(directory.Path, client, fileMutator: mutator);
        var installation = await store.AcquireAsync(descriptor);
        var paths = store.GetPaths(descriptor);

        var failure = await Assert.ThrowsAsync<ModelStoreException>(() => store.RemoveActiveAsync(descriptor));

        Assert.Equal(ModelStoreError.StorageWriteFailed, failure.Error);
        Assert.False(File.Exists(paths.ActiveManifestPath));
        Assert.False(File.Exists(installation.Installation.PayloadPath));
        Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(paths.ActiveManifestPath)!,
            ".removing-*.json"));

        var recovery = await store.RecoverRemovalsAndCollectOrphansAsync();
        Assert.Equal(1, recovery.RemovalTombstonesRecovered);
        Assert.False(File.Exists(paths.ActiveManifestPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(paths.ActiveManifestPath)!,
            ".removing-*.json"));
    }

    [Fact]
    public async Task FormatMismatchDeletesInvalidPartial()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(65, seed: 88);
        Array.Clear(payload, 0, sizeof(uint));
        var descriptor = CreateDescriptor("format", payload);
        using var client = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, payload)));
        await using var store = CreateStore(directory.Path, client);

        var failure = await Assert.ThrowsAsync<ModelStoreException>(() => store.AcquireAsync(descriptor));

        Assert.Equal(ModelStoreError.PayloadFormatMismatch, failure.Error);
        var paths = store.GetPaths(descriptor);
        Assert.False(File.Exists(paths.PartialPath));
        Assert.False(File.Exists(paths.PartialStatePath));
        Assert.False(File.Exists(paths.PayloadPath));
    }

    [Fact]
    public async Task ValidMagicCannotActivateWithoutSuccessfulFullRuntimeLoad()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(85, seed: 89);
        var descriptor = CreateDescriptor("abi-reject", payload);
        var rejectingVerifier = new RejectingActivationVerifier();
        var handler = new ScriptedHandler((_, requestNumber, _) => requestNumber == 1
            ? Response(HttpStatusCode.OK, payload)
            : throw new InvalidOperationException("Cached activation must not use the network."));
        using var client = new HttpClient(handler);
        await using (var rejectedStore = CreateStore(
                         directory.Path,
                         client,
                         activationVerifier: rejectingVerifier))
        {
            var failure = await Assert.ThrowsAsync<ModelStoreException>(() =>
                rejectedStore.AcquireAsync(descriptor));
            Assert.Equal(ModelStoreError.ActivationFailed, failure.Error);
            Assert.Equal(1, rejectingVerifier.CallCount);
            var paths = rejectedStore.GetPaths(descriptor);
            Assert.True(File.Exists(paths.PayloadPath));
            Assert.False(File.Exists(paths.ActiveManifestPath));
        }

        var acceptingVerifier = new DeterministicFullLoadVerifier();
        await using var acceptedStore = CreateStore(
            directory.Path,
            client,
            activationVerifier: acceptingVerifier);

        var accepted = await acceptedStore.AcquireAsync(descriptor);

        Assert.Equal(ModelAcquisitionDisposition.ActivatedCachedPayload, accepted.Disposition);
        Assert.Equal(1, acceptingVerifier.CallCount);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DiskChecksFailBeforeNetworkAccess()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(61, seed: 99);
        var descriptor = CreateDescriptor("disk", payload);
        var handler = new ScriptedHandler((_, _, _) => throw new InvalidOperationException("Network must remain idle."));
        using var client = new HttpClient(handler);
        await using var store = CreateStore(
            directory.Path,
            client,
            storageProbe: new FixedStorageProbe(writable: true, availableBytes: payload.Length - 1));

        var failure = await Assert.ThrowsAsync<ModelStoreException>(() => store.AcquireAsync(descriptor));

        Assert.Equal(ModelStoreError.InsufficientDiskSpace, failure.Error);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task WriteCheckFailsBeforeCreatingDownloadOrUsingNetwork()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(59, seed: 101);
        var descriptor = CreateDescriptor("readonly", payload);
        var handler = new ScriptedHandler((_, _, _) => throw new InvalidOperationException("Network must remain idle."));
        using var client = new HttpClient(handler);
        await using var store = CreateStore(
            directory.Path,
            client,
            storageProbe: new FixedStorageProbe(writable: false, availableBytes: long.MaxValue));

        var failure = await Assert.ThrowsAsync<ModelStoreException>(() => store.AcquireAsync(descriptor));

        Assert.Equal(ModelStoreError.StorageNotWritable, failure.Error);
        Assert.Equal(0, handler.RequestCount);
        var paths = store.GetPaths(descriptor);
        Assert.False(File.Exists(paths.PartialPath));
        Assert.False(File.Exists(paths.PartialStatePath));
    }

    [Fact]
    public async Task TransientFailuresStopAtConfiguredRetryBound()
    {
        using var directory = new TemporaryDirectory();
        var payload = CreatePayload(57, seed: 111);
        var descriptor = CreateDescriptor("retry", payload);
        var handler = new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.ServiceUnavailable, []));
        using var client = new HttpClient(handler);
        await using var store = CreateStore(directory.Path, client, maximumAttempts: 3);

        var failure = await Assert.ThrowsAsync<ModelStoreException>(() => store.AcquireAsync(descriptor));

        Assert.Equal(ModelStoreError.TransportRejected, failure.Error);
        Assert.Equal(3, handler.RequestCount);
    }

    private static async Task SeedPartialAsync(
        string rootPath,
        WhisperModelDescriptor descriptor,
        byte[] prefix)
    {
        using var client = new HttpClient(new ScriptedHandler((_, _, _) =>
            Response(HttpStatusCode.OK, prefix)));
        await using var store = CreateStore(rootPath, client, maximumAttempts: 1);
        var failure = await Assert.ThrowsAsync<ModelStoreException>(() => store.AcquireAsync(descriptor));
        Assert.Equal(ModelStoreError.DownloadIncomplete, failure.Error);
    }

    private static WhisperModelStore CreateStore(
        string rootPath,
        HttpClient client,
        int maximumAttempts = 2,
        IModelStorageProbe? storageProbe = null,
        IWhisperModelActivationVerifier? activationVerifier = null,
        IModelPayloadRetentionLeaseProvider? retentionLeaseProvider = null,
        IModelStoreFileMutator? fileMutator = null,
        TimeSpan? downloadAttemptTimeout = null) => new(
        rootPath,
        client,
        activationVerifier ?? new DeterministicFullLoadVerifier(),
        retentionLeaseProvider ?? new TestRetentionLeaseProvider(),
        new WhisperModelStoreOptions
        {
            MaximumDownloadAttempts = maximumAttempts,
            RetryDelay = TimeSpan.Zero,
            DownloadAttemptTimeout = downloadAttemptTimeout ?? TimeSpan.FromSeconds(5),
            SafetyMarginBytes = 0,
        },
        storageProbe ?? new FixedStorageProbe(writable: true, availableBytes: long.MaxValue),
        fileMutator: fileMutator);

    private static WhisperModelDescriptor CreateDescriptor(
        string id,
        byte[] payload,
        string catalogVersion = "test-1") => new(
        id,
        "test",
        $"test/{id}",
        $"{id}.bin",
        new Uri($"https://models.example.test/{id}/{catalogVersion}.bin", UriKind.Absolute),
        payload.LongLength,
        Hash(payload),
        1024,
        Multilingual: true,
        Recommended: false,
        catalogVersion,
        "0123456789abcdef0123456789abcdef01234567",
        new WhisperModelFormat("whisper.cpp.ggml-f16.v1", 0x67676d6cU, new Version(1, 9, 1)),
        "MIT",
        new Uri("https://licenses.example.test/whisper"),
        new Uri("https://models.example.test"),
        new Uri("https://models.example.test/origin"),
        "Test fixture generated in memory.");

    private static byte[] CreatePayload(int length, int seed)
    {
        var payload = new byte[length];
        var random = new Random(seed);
        random.NextBytes(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x67676d6cU);
        return payload;
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, byte[] content) =>
        Response(statusCode, new ByteArrayContent(content));

    private static HttpResponseMessage Response(HttpStatusCode statusCode, HttpContent content) => new(statusCode)
    {
        Content = content,
    };

    private static string Hash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static async Task<string> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private sealed class CompletionProgress(long target) : IProgress<long>
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(long value)
        {
            if (value >= target)
            {
                Completed.TrySetResult();
            }
        }
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, int, CancellationToken, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(responseFactory(request, requestNumber, cancellationToken));
        }
    }

    private sealed class FixedStorageProbe(bool writable, long availableBytes) : IModelStorageProbe
    {
        public ValueTask EnsureWritableAsync(string directoryPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!writable)
            {
                throw new ModelStoreException(
                    ModelStoreError.StorageNotWritable,
                    "The fake storage is read-only.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<long> GetAvailableFreeSpaceAsync(
            string directoryPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(availableBytes);
        }
    }

    private sealed class DeterministicFullLoadVerifier : IWhisperModelActivationVerifier
    {
        public int CallCount { get; private set; }

        public async ValueTask VerifyCanLoadAsync(
            string payloadPath,
            WhisperModelDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            CallCount++;
            await using var stream = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!actual.Equals(descriptor.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The deterministic full-load fixture rejected the payload.");
            }
        }
    }

    private sealed class RejectingActivationVerifier : IWhisperModelActivationVerifier
    {
        public int CallCount { get; private set; }

        public ValueTask VerifyCanLoadAsync(
            string payloadPath,
            WhisperModelDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Synthetic native ABI/load rejection.");
        }
    }

    private sealed class TestRetentionLeaseProvider : IModelPayloadRetentionLeaseProvider
    {
        private readonly HashSet<string> _retained = new(StringComparer.Ordinal);

        public void Retain(string sha256) => _retained.Add(sha256);

        public void Release(string sha256) => _retained.Remove(sha256);

        public ValueTask<IModelPayloadUsageLease> AcquireUsageLeaseAsync(
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IModelPayloadUsageLease>(new UsageLease());
        }

        public ValueTask<IModelPayloadRetentionLease> AcquireDeletionLeaseAsync(
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IModelPayloadRetentionLease>(new Lease(_retained.Contains(sha256)));
        }

        private sealed class Lease(bool isRetained) : IModelPayloadRetentionLease
        {
            public bool IsRetained { get; } = isRetained;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class UsageLease : IModelPayloadUsageLease
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FailingRetentionLeaseProvider : IModelPayloadRetentionLeaseProvider
    {
        public ValueTask<IModelPayloadUsageLease> AcquireUsageLeaseAsync(
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IModelPayloadUsageLease>(new UsageLease());
        }

        public ValueTask<IModelPayloadRetentionLease> AcquireDeletionLeaseAsync(
            string sha256,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic durable retention outage.");

        private sealed class UsageLease : IModelPayloadUsageLease
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class OrderingRetentionLeaseProvider : IModelPayloadRetentionLeaseProvider
    {
        private TaskCompletionSource _usageReleased = CompletedSignal();

        public TaskCompletionSource DeletionLeaseRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IModelPayloadUsageLease> AcquireUsageLeaseAsync(
            string sha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _usageReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return ValueTask.FromResult<IModelPayloadUsageLease>(new UsageLease(this));
        }

        public async ValueTask<IModelPayloadRetentionLease> AcquireDeletionLeaseAsync(
            string sha256,
            CancellationToken cancellationToken)
        {
            DeletionLeaseRequested.TrySetResult();
            await _usageReleased.Task.WaitAsync(cancellationToken);
            return new Lease();
        }

        private static TaskCompletionSource CompletedSignal()
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult();
            return signal;
        }

        private sealed class Lease : IModelPayloadRetentionLease
        {
            public bool IsRetained => false;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class UsageLease(OrderingRetentionLeaseProvider owner) : IModelPayloadUsageLease
        {
            public ValueTask DisposeAsync()
            {
                owner._usageReleased.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TombstoneDeleteFaultMutator : IModelStoreFileMutator
    {
        private readonly PhysicalModelStoreFileMutator _physical = new();
        private bool _failNextTombstoneDelete = true;

        public bool FileExists(string path) => _physical.FileExists(path);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite = false) =>
            _physical.MoveFile(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path)
        {
            if (_failNextTombstoneDelete
                && Path.GetFileName(path).StartsWith(".removing-", StringComparison.Ordinal))
            {
                _failNextTombstoneDelete = false;
                throw new IOException("Synthetic tombstone cleanup failure.");
            }

            _physical.DeleteFile(path);
        }
    }

    private sealed class FullThenWaitStream(byte[] payload) : Stream
    {
        private bool _returnedPayload;

        public TaskCompletionSource PayloadWasRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_returnedPayload)
            {
                _returnedPayload = true;
                payload.CopyTo(buffer);
                PayloadWasRead.TrySetResult();
                return ValueTask.FromResult(payload.Length);
            }

            return WaitForCancellationAsync(cancellationToken);
        }

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FullThenIgnoreCancellationStream(byte[] payload) : Stream
    {
        private readonly TaskCompletionSource<int> _blockedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _returnedPayload;

        public TaskCompletionSource PayloadWasRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_returnedPayload)
            {
                _returnedPayload = true;
                payload.CopyTo(buffer);
                PayloadWasRead.TrySetResult();
                return ValueTask.FromResult(payload.Length);
            }

            return new ValueTask<int>(_blockedRead.Task);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _blockedRead.TrySetResult(0);
            }

            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"isTranscribe-local-model-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
