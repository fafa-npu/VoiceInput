using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using VoiceInput.Models;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class FunAsrRuntimeManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFilesReportNotInstalled()
    {
        using var manager = CreateManager(new BytesHandler([]));

        Assert.False(manager.IsInstalled(FunAsrModelCatalog.DefaultId));
    }

    [Fact]
    public async Task DownloadWritesVerifiedArtifactAtomically()
    {
        byte[] content = "verified model"u8.ToArray();
        using var manager = CreateManager(new BytesHandler(content));
        var artifact = Artifact(content);

        await manager.DownloadArtifactAsync(artifact, CancellationToken.None);

        string finalPath = Path.Combine(_root, "models", "test.gguf");
        Assert.Equal(content, File.ReadAllBytes(finalPath));
        Assert.False(File.Exists(finalPath + ".part"));
    }

    [Fact]
    public async Task DownloadResumesExistingPartFile()
    {
        byte[] content = "resumable content"u8.ToArray();
        var handler = new BytesHandler(content);
        using var manager = CreateManager(handler);
        var artifact = Artifact(content);
        string partPath = Path.Combine(_root, "models", "test.gguf.part");
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        File.WriteAllBytes(partPath, content[..5]);

        await manager.DownloadArtifactAsync(artifact, CancellationToken.None);

        Assert.Equal(5, handler.LastRangeFrom);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(_root, "models", "test.gguf")));
    }

    [Fact]
    public async Task HashMismatchDoesNotActivateArtifact()
    {
        byte[] content = "wrong content"u8.ToArray();
        using var manager = CreateManager(new BytesHandler(content));
        var artifact = new FunAsrArtifact(
            "models/test.gguf", new("https://example.test/model"), content.Length, new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.DownloadArtifactAsync(artifact, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_root, "models", "test.gguf")));
    }

    [Fact]
    public async Task InsufficientSpaceFailsBeforeDownload()
    {
        byte[] content = "model"u8.ToArray();
        var handler = new BytesHandler(content);
        using var manager = CreateManager(handler, availableBytes: () => 0);

        await Assert.ThrowsAsync<IOException>(
            () => manager.DownloadArtifactAsync(Artifact(content), CancellationToken.None));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CancellationPreservesPartFile()
    {
        byte[] content = "partially downloaded model"u8.ToArray();
        using var manager = CreateManager(new PausingHandler(content));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.DownloadArtifactAsync(Artifact(content), cancellation.Token));

        string partPath = Path.Combine(_root, "models", "test.gguf.part");
        Assert.True(File.Exists(partPath));
        Assert.True(new FileInfo(partPath).Length > 0);
        Assert.False(File.Exists(Path.Combine(_root, "models", "test.gguf")));
    }

    [Fact]
    public void FilesWithoutSuccessfulInstallationDoNotReportInstalled()
    {
        using var manager = CreateManager(new BytesHandler([]));
        FunAsrModelDefinition model = FunAsrModelCatalog.Default;
        foreach (FunAsrArtifact artifact in model.Artifacts.Append(FunAsrModelCatalog.Vad))
            CreateSizedFile(artifact.RelativePath, artifact.Size);

        Assert.False(manager.IsInstalled(model.Id));

        CreateRuntimeFiles();
        Assert.False(manager.IsInstalled(model.Id));
    }

    [Fact]
    public void ResolveUsesRunnerSpecificExecutable()
    {
        using var manager = CreateManager(new BytesHandler([]));
        FunAsrModelDefinition model = FunAsrModelCatalog.Default;
        foreach (FunAsrArtifact artifact in model.Artifacts.Append(FunAsrModelCatalog.Vad))
            CreateSizedFile(artifact.RelativePath, artifact.Size);
        CreateRuntimeFiles();

        FunAsrResolvedModel resolved = manager.Resolve(model.Id);

        Assert.EndsWith("llama-funasr-sensevoice.exe", resolved.ExecutablePath);
        Assert.EndsWith("fsmn-vad.gguf", resolved.VadPath);
        Assert.Equal(model.Artifacts.Count, resolved.ArtifactPaths.Count);
    }

    [Fact]
    public void RemoveDeletesOnlySelectedModel()
    {
        using var manager = CreateManager(new BytesHandler([]));
        FunAsrArtifact selected = FunAsrModelCatalog.Default.Artifacts[0];
        FunAsrArtifact other = FunAsrModelCatalog.Get("paraformer-zh-q8").Artifacts[0];
        CreateSizedFile(selected.RelativePath, 1);
        CreateSizedFile(other.RelativePath, 1);

        manager.Remove(FunAsrModelCatalog.DefaultId);

        Assert.False(File.Exists(Path.Combine(_root, selected.RelativePath)));
        Assert.True(File.Exists(Path.Combine(_root, other.RelativePath)));
    }

    [Fact]
    public async Task RuntimeArchiveMustContainEveryRequiredExecutable()
    {
        byte[] archive = RuntimeArchive("llama-funasr-sensevoice.exe");
        using var manager = CreateManager(new BytesHandler(archive));
        var artifact = Artifact(archive, "downloads/runtime.zip");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.InstallRuntimeArchiveAsync(artifact, CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_root, "runtime", FunAsrModelCatalog.RuntimeVersion)));
    }

    [Fact]
    public async Task RuntimeArchiveActivatesAfterValidation()
    {
        byte[] archive = RuntimeArchive(
            "llama-funasr-sensevoice.exe",
            "llama-funasr-paraformer.exe",
            "llama-funasr-cli.exe");
        using var manager = CreateManager(new BytesHandler(archive));

        await manager.InstallRuntimeArchiveAsync(
            Artifact(archive, "downloads/runtime.zip"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(
            _root, "runtime", FunAsrModelCatalog.RuntimeVersion, "llama-funasr-cli.exe")));
    }

    [Fact]
    public async Task CorruptRuntimeDoesNotReplaceVerifiedRuntime()
    {
        byte[] validArchive = RuntimeArchive(
            "llama-funasr-sensevoice.exe",
            "llama-funasr-paraformer.exe",
            "llama-funasr-cli.exe");
        byte[] corruptArchive = RuntimeArchive("llama-funasr-sensevoice.exe");
        var handler = new MappedBytesHandler(new Dictionary<string, byte[]>
        {
            ["/valid"] = validArchive,
            ["/corrupt"] = corruptArchive,
        });
        using var manager = CreateManager(handler);

        await manager.InstallRuntimeArchiveAsync(
            Artifact(validArchive, "downloads/valid.zip", "/valid"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallRuntimeArchiveAsync(
            Artifact(corruptArchive, "downloads/corrupt.zip", "/corrupt"), CancellationToken.None));

        string runtimePath = Path.Combine(
            _root, "runtime", FunAsrModelCatalog.RuntimeVersion, "llama-funasr-cli.exe");
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(runtimePath));
    }

    [Fact]
    public async Task InstallMarksModelInstalledOnlyAfterSmokeTest()
    {
        TestCatalog catalog = CreateTestCatalog();
        var stages = new List<FunAsrInstallStage>();
        bool smokeTested = false;
        using var manager = CreateManager(
            new MappedBytesHandler(catalog.Content),
            smokeTest: (resolved, _) =>
            {
                Assert.Equal(catalog.Model.Id, resolved.Definition.Id);
                smokeTested = true;
                return Task.CompletedTask;
            },
            catalog: catalog);
        manager.ProgressChanged += progress => stages.Add(progress.Stage);

        await manager.InstallAsync(catalog.Model.Id, CancellationToken.None);

        Assert.True(smokeTested);
        Assert.True(manager.IsInstalled(catalog.Model.Id));
        Assert.Contains(FunAsrInstallStage.Verifying, stages);
        Assert.Contains(FunAsrInstallStage.Testing, stages);
        Assert.Equal(FunAsrInstallStage.Installed, stages[^1]);
        Assert.True(File.Exists(Path.Combine(_root, "installation.json")));
    }

    [Fact]
    public async Task SmokeTestFailureDoesNotMarkModelInstalled()
    {
        TestCatalog catalog = CreateTestCatalog();
        FunAsrInstallProgress? lastProgress = null;
        using var manager = CreateManager(
            new MappedBytesHandler(catalog.Content),
            smokeTest: (_, _) => throw new InvalidOperationException("smoke failed"),
            catalog: catalog);
        manager.ProgressChanged += progress => lastProgress = progress;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.InstallAsync(catalog.Model.Id, CancellationToken.None));

        Assert.False(manager.IsInstalled(catalog.Model.Id));
        Assert.Equal(FunAsrInstallStage.Failed, lastProgress?.Stage);
        Assert.Contains("smoke failed", lastProgress?.Error);
    }

    [Fact]
    public void MalformedManifestFieldsReportNotInstalled()
    {
        TestCatalog catalog = CreateTestCatalog();
        using var manager = CreateManager(new MappedBytesHandler(catalog.Content), catalog: catalog);
        CreateSizedFile(catalog.Vad.RelativePath, catalog.Vad.Size);
        foreach (FunAsrArtifact artifact in catalog.Model.Artifacts)
            CreateSizedFile(artifact.RelativePath, artifact.Size);
        CreateRuntimeFiles();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "installation.json"), JsonSerializer.Serialize(new
        {
            RuntimeVersion = FunAsrModelCatalog.RuntimeVersion,
            RuntimeSha256 = catalog.Runtime.Sha256,
            Vad = new
            {
                catalog.Vad.RelativePath,
                catalog.Vad.Size,
                catalog.Vad.Sha256,
            },
            Models = new Dictionary<string, object>
            {
                [catalog.Model.Id] = new[]
                {
                    new
                    {
                        RelativePath = (string?)null,
                        catalog.Model.Artifacts[0].Size,
                        catalog.Model.Artifacts[0].Sha256,
                    },
                },
            },
        }));

        Assert.False(manager.IsInstalled(catalog.Model.Id));
    }

    [Fact]
    public async Task RemoveFailsImmediatelyWhileInstallationIsActive()
    {
        byte[] content = "runtime download"u8.ToArray();
        var handler = new PausingHandler(content);
        using var manager = CreateManager(handler);
        using var cancellation = new CancellationTokenSource();
        Task installing = manager.InstallRuntimeArchiveAsync(
            Artifact(content, "downloads/runtime.zip"), cancellation.Token);
        await handler.Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopwatch = Stopwatch.StartNew();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => manager.Remove(FunAsrModelCatalog.DefaultId));
        stopwatch.Stop();

        Assert.Contains("installation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installing);
    }

    [Fact]
    public async Task SameSizeModelCorruptionReportsNotInstalled()
    {
        TestCatalog catalog = CreateTestCatalog();
        using var manager = CreateManager(
            new MappedBytesHandler(catalog.Content),
            smokeTest: (_, _) => Task.CompletedTask,
            catalog: catalog);
        await manager.InstallAsync(catalog.Model.Id, CancellationToken.None);
        Assert.True(manager.IsInstalled(catalog.Model.Id));
        string modelPath = Path.Combine(_root, catalog.Model.Artifacts[0].RelativePath);

        File.WriteAllBytes(modelPath, "wrong"u8.ToArray());
        File.SetLastWriteTimeUtc(modelPath, DateTime.UtcNow.AddSeconds(1));

        Assert.False(manager.IsInstalled(catalog.Model.Id));
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeReadingBody()
    {
        byte[] expected = "model"u8.ToArray();
        var handler = new OversizedHandler(expected, expected.Length + 1L);
        using var manager = CreateManager(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.DownloadArtifactAsync(Artifact(expected), CancellationToken.None));

        Assert.Equal(0, handler.ReadCount);
        Assert.False(File.Exists(Path.Combine(_root, "models", "test.gguf")));
    }

    private FunAsrRuntimeManager CreateManager(
        HttpMessageHandler handler,
        Func<long>? availableBytes = null,
        Func<FunAsrResolvedModel, CancellationToken, Task>? smokeTest = null,
        TestCatalog? catalog = null) =>
        new(
            _root,
            new HttpClient(handler),
            availableBytes ?? (() => long.MaxValue),
            smokeTest,
            catalog is null ? null : _ => catalog.Model,
            catalog?.Runtime,
            catalog?.Vad);

    private static FunAsrArtifact Artifact(
        byte[] content, string relativePath = "models/test.gguf", string uriPath = "/model") => new(
        relativePath,
        new($"https://example.test{uriPath}"),
        content.Length,
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    private static TestCatalog CreateTestCatalog()
    {
        byte[] runtimeContent = RuntimeArchive(
            "llama-funasr-sensevoice.exe",
            "llama-funasr-paraformer.exe",
            "llama-funasr-cli.exe");
        byte[] vadContent = "vad"u8.ToArray();
        byte[] modelContent = "model"u8.ToArray();
        FunAsrArtifact runtime = Artifact(runtimeContent, "downloads/runtime.zip", "/runtime");
        FunAsrArtifact vad = Artifact(vadContent, "shared/fsmn-vad.gguf", "/vad");
        FunAsrArtifact modelArtifact = Artifact(
            modelContent, "models/test-model/model.gguf", "/model");
        var model = new FunAsrModelDefinition(
            "test-model",
            "Test model",
            "Test model",
            FunAsrRunnerKind.SenseVoice,
            new HashSet<string>(["zh-CN"], StringComparer.OrdinalIgnoreCase),
            [modelArtifact],
            new("https://example.test/source"),
            new("https://example.test/license"));
        return new(
            model,
            runtime,
            vad,
            new Dictionary<string, byte[]>
            {
                ["/runtime"] = runtimeContent,
                ["/vad"] = vadContent,
                ["/model"] = modelContent,
            });
    }

    private void CreateRuntimeFiles()
    {
        string directory = Path.Combine(_root, "runtime", FunAsrModelCatalog.RuntimeVersion);
        Directory.CreateDirectory(directory);
        foreach (string executable in new[]
        {
            "llama-funasr-sensevoice.exe",
            "llama-funasr-paraformer.exe",
            "llama-funasr-cli.exe",
        })
            File.WriteAllBytes(Path.Combine(directory, executable), [1]);
    }

    private void CreateSizedFile(string relativePath, long size)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(size);
    }

    private static byte[] RuntimeArchive(params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string name in entries)
            {
                using Stream entry = archive.CreateEntry(name).Open();
                entry.WriteByte(1);
            }
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class BytesHandler(byte[] content) : HttpMessageHandler
    {
        public long? LastRangeFrom { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRangeFrom = request.Headers.Range?.Ranges.Single().From;
            int offset = checked((int)(LastRangeFrom ?? 0));
            var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content[offset..]),
            };
            response.Content.Headers.ContentLength = content.Length - offset;
            if (offset > 0)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class MappedBytesHandler(IReadOnlyDictionary<string, byte[]> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] bytes = content[request.RequestUri!.AbsolutePath];
            long offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            var response = new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes[checked((int)offset)..]),
            };
            response.Content.Headers.ContentLength = bytes.Length - offset;
            return Task.FromResult(response);
        }
    }

    private sealed class PausingHandler(byte[] content) : HttpMessageHandler
    {
        public TaskCompletionSource Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PausingStream(content[..4])),
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class PausingStream(byte[] firstChunk) : Stream
    {
        private bool _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_read)
            {
                _read = true;
                firstChunk.CopyTo(buffer);
                return firstChunk.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OversizedHandler(byte[] content, long declaredLength) : HttpMessageHandler
    {
        public int ReadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new TrackingStream(content, () => ReadCount++)),
            };
            response.Content.Headers.ContentLength = declaredLength;
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingStream(byte[] content, Action onRead) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed record TestCatalog(
        FunAsrModelDefinition Model,
        FunAsrArtifact Runtime,
        FunAsrArtifact Vad,
        IReadOnlyDictionary<string, byte[]> Content);
}
