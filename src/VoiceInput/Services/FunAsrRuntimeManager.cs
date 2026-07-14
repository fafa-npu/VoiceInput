using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceInput.Models;

namespace VoiceInput.Services;

internal enum FunAsrInstallStage
{
    NotInstalled,
    Downloading,
    Verifying,
    Testing,
    Installed,
    Failed,
}

internal sealed record FunAsrInstallProgress(
    string ModelId,
    FunAsrInstallStage Stage,
    string Artifact,
    long DownloadedBytes,
    long? TotalBytes,
    string? Error = null);

internal sealed record FunAsrResolvedModel(
    FunAsrModelDefinition Definition,
    string ExecutablePath,
    string VadPath,
    IReadOnlyDictionary<string, string> ArtifactPaths);

internal sealed class FunAsrRuntimeManager : IDisposable
{
    private const long StagingMargin = 64L * 1024 * 1024;
    private static readonly string[] RequiredExecutables =
    [
        "llama-funasr-sensevoice.exe",
        "llama-funasr-paraformer.exe",
        "llama-funasr-cli.exe",
    ];

    private readonly HttpClient _httpClient;
    private readonly Func<long> _availableBytes;
    private readonly Func<FunAsrResolvedModel, CancellationToken, Task> _smokeTest;
    private readonly Func<string, FunAsrModelDefinition> _getModel;
    private readonly FunAsrArtifact _runtimeArtifact;
    private readonly FunAsrArtifact _vadArtifact;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _verificationGate = new();
    private readonly Dictionary<string, VerifiedFile> _verifiedFiles =
        new(StringComparer.OrdinalIgnoreCase);

    public FunAsrRuntimeManager()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceInput", "FunASR"),
            new HttpClient(),
            null,
            null,
            null,
            null,
            null)
    {
    }

    internal FunAsrRuntimeManager(
        string rootPath,
        HttpClient httpClient,
        Func<long>? availableBytes = null,
        Func<FunAsrResolvedModel, CancellationToken, Task>? smokeTest = null,
        Func<string, FunAsrModelDefinition>? getModel = null,
        FunAsrArtifact? runtimeArtifact = null,
        FunAsrArtifact? vadArtifact = null)
    {
        RootPath = Path.GetFullPath(rootPath);
        _httpClient = httpClient;
        _availableBytes = availableBytes ?? (() => new DriveInfo(Path.GetPathRoot(RootPath)!).AvailableFreeSpace);
        _smokeTest = smokeTest ?? RunSmokeTestAsync;
        _getModel = getModel ?? FunAsrModelCatalog.Get;
        _runtimeArtifact = runtimeArtifact ?? FunAsrModelCatalog.Runtime;
        _vadArtifact = vadArtifact ?? FunAsrModelCatalog.Vad;
    }

    public string RootPath { get; }

    public event Action<FunAsrInstallProgress>? ProgressChanged;

    public bool IsInstalled(string modelId)
    {
        FunAsrModelDefinition model = _getModel(modelId);
        InstallationManifest manifest = LoadManifest();
        return manifest.RuntimeVersion == FunAsrModelCatalog.RuntimeVersion
            && string.Equals(manifest.RuntimeSha256, _runtimeArtifact.Sha256, StringComparison.OrdinalIgnoreCase)
            && manifest.Vad is not null
            && manifest.Vad.Matches(_vadArtifact)
            && manifest.Models is not null
            && manifest.Models.TryGetValue(model.Id, out List<InstalledArtifact>? installedArtifacts)
            && installedArtifacts is not null
            && ArtifactsMatch(installedArtifacts, model.Artifacts)
            && model.Artifacts.All(ArtifactIsVerified)
            && ArtifactIsVerified(_vadArtifact)
            && RuntimeFilesAreVerified(manifest.RuntimeFiles);
    }

    public FunAsrResolvedModel Resolve(string modelId)
    {
        FunAsrModelDefinition model = _getModel(modelId);
        return ResolveFiles(model);
    }

    private FunAsrResolvedModel ResolveFiles(FunAsrModelDefinition model)
    {
        string executable = model.Runner switch
        {
            FunAsrRunnerKind.SenseVoice => "llama-funasr-sensevoice.exe",
            FunAsrRunnerKind.Paraformer => "llama-funasr-paraformer.exe",
            FunAsrRunnerKind.Nano => "llama-funasr-cli.exe",
            _ => throw new InvalidOperationException($"Unsupported FunASR runner '{model.Runner}'."),
        };
        if (!model.Artifacts.All(ArtifactExists)
            || !ArtifactExists(_vadArtifact)
            || !RequiredExecutables.All(item => File.Exists(GetRuntimePath(item))))
        {
            throw new InvalidOperationException($"FunASR model '{model.Id}' is not installed.");
        }

        var artifactPaths = model.Artifacts.ToDictionary(
            artifact => artifact.RelativePath,
            artifact => ResolvePath(artifact.RelativePath),
            StringComparer.OrdinalIgnoreCase);

        return new(
            model,
            GetRuntimePath(executable),
            ResolvePath(_vadArtifact.RelativePath),
            artifactPaths);
    }

    public void Remove(string modelId)
    {
        if (!_operationGate.Wait(0))
            throw new InvalidOperationException("Wait for the active FunASR installation to finish or cancel it first.");
        try
        {
            FunAsrModelDefinition model = _getModel(modelId);
            foreach (FunAsrArtifact artifact in model.Artifacts)
            {
                string path = ResolvePath(artifact.RelativePath);
                File.Delete(path);
                File.Delete(path + ".part");
            }

            InstallationManifest manifest = LoadManifest();
            if (manifest.Models?.Remove(model.Id) == true)
                SaveManifest(manifest);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task InstallAsync(string modelId, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FunAsrModelDefinition model = _getModel(modelId);
            if (IsInstalled(model.Id))
            {
                Report(model.Id, FunAsrInstallStage.Installed, string.Empty, model.DownloadSize, model.DownloadSize);
                return;
            }

            try
            {
                await InstallRuntimeArchiveCoreAsync(_runtimeArtifact, model.Id, cancellationToken).ConfigureAwait(false);
                await DownloadArtifactAsync(_vadArtifact, model.Id, cancellationToken).ConfigureAwait(false);
                foreach (FunAsrArtifact artifact in model.Artifacts)
                    await DownloadArtifactAsync(artifact, model.Id, cancellationToken).ConfigureAwait(false);

                Report(model.Id, FunAsrInstallStage.Testing, model.DisplayName, 0, null);
                await _smokeTest(ResolveFiles(model), cancellationToken).ConfigureAwait(false);
                MarkInstalled(model);
                Report(model.Id, FunAsrInstallStage.Installed, model.DisplayName, model.DownloadSize, model.DownloadSize);
            }
            catch (OperationCanceledException)
            {
                Report(model.Id, FunAsrInstallStage.NotInstalled, string.Empty, 0, null);
                throw;
            }
            catch (Exception exception)
            {
                Report(model.Id, FunAsrInstallStage.Failed, string.Empty, 0, null, exception.Message);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async Task InstallRuntimeArchiveAsync(
        FunAsrArtifact artifact, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InstallRuntimeArchiveCoreAsync(artifact, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task InstallRuntimeArchiveCoreAsync(
        FunAsrArtifact artifact, string modelId, CancellationToken cancellationToken)
    {
        InstallationManifest manifest = LoadManifest();
        string destinationPath = ResolvePath(Path.Combine("runtime", FunAsrModelCatalog.RuntimeVersion));
        if (manifest.RuntimeVersion == FunAsrModelCatalog.RuntimeVersion
            && string.Equals(manifest.RuntimeSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            && RuntimeIsComplete(destinationPath)
            && RuntimeFilesAreVerified(manifest.RuntimeFiles))
        {
            return;
        }

        await DownloadArtifactAsync(artifact, modelId, cancellationToken).ConfigureAwait(false);

        string archivePath = ResolvePath(artifact.RelativePath);
        string runtimeRoot = ResolvePath("runtime");
        string stagingPath = ResolvePath(Path.Combine("runtime", $".staging-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(stagingPath);

        try
        {
            ExtractArchive(archivePath, stagingPath);
            string payloadPath = FindRuntimePayload(stagingPath)
                ?? throw new InvalidDataException("The FunASR runtime archive is missing required executables.");

            string backupPath = destinationPath + $".backup-{Guid.NewGuid():N}";
            bool movedExisting = false;
            try
            {
                if (Directory.Exists(destinationPath))
                {
                    Directory.Move(destinationPath, backupPath);
                    movedExisting = true;
                }

                Directory.Move(payloadPath, destinationPath);
                if (movedExisting)
                    Directory.Delete(backupPath, recursive: true);
            }
            catch
            {
                if (!Directory.Exists(destinationPath) && movedExisting && Directory.Exists(backupPath))
                    Directory.Move(backupPath, destinationPath);
                throw;
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The FunASR runtime archive could not be installed.", exception);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);
        }
    }

    internal Task DownloadArtifactAsync(FunAsrArtifact artifact, CancellationToken cancellationToken) =>
        DownloadArtifactAsync(artifact, string.Empty, cancellationToken);

    private async Task DownloadArtifactAsync(
        FunAsrArtifact artifact, string modelId, CancellationToken cancellationToken)
    {
        string finalPath = ResolvePath(artifact.RelativePath);
        string partPath = finalPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        if (File.Exists(finalPath)
            && await MatchesAsync(finalPath, artifact, cancellationToken).ConfigureAwait(false))
            return;

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existing > artifact.Size)
        {
            File.Delete(partPath);
            existing = 0;
        }

        if (existing == artifact.Size)
        {
            await ActivateVerifiedPartAsync(partPath, finalPath, artifact, modelId, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        long remaining = artifact.Size - existing;
        long available = _availableBytes();
        if (available < StagingMargin || remaining > available - StagingMargin)
            throw new IOException($"Not enough disk space to download {Path.GetFileName(artifact.RelativePath)}.");

        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.Url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
            existing = 0;

        long expectedResponseBytes = artifact.Size - existing;
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > expectedResponseBytes)
        {
            throw new InvalidDataException(
                $"Download for {Path.GetFileName(artifact.RelativePath)} exceeds the pinned artifact size.");
        }
        if (append)
        {
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range?.From != existing || range.To != artifact.Size - 1 || range.Length != artifact.Size)
            {
                throw new InvalidDataException(
                    $"Invalid resume range for {Path.GetFileName(artifact.RelativePath)}.");
            }
        }

        long total = artifact.Size;
        using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using (var destination = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = new byte[81920];
            long downloaded = existing;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > artifact.Size - downloaded)
                {
                    throw new InvalidDataException(
                        $"Download for {Path.GetFileName(artifact.RelativePath)} exceeds the pinned artifact size.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                Report(
                    modelId,
                    FunAsrInstallStage.Downloading,
                    Path.GetFileName(artifact.RelativePath),
                    downloaded,
                    total);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await ActivateVerifiedPartAsync(partPath, finalPath, artifact, modelId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ActivateVerifiedPartAsync(
        string partPath,
        string finalPath,
        FunAsrArtifact artifact,
        string modelId,
        CancellationToken cancellationToken)
    {
        Report(
            modelId,
            FunAsrInstallStage.Verifying,
            Path.GetFileName(artifact.RelativePath),
            artifact.Size,
            artifact.Size);

        if (!await MatchesAsync(partPath, artifact, cancellationToken).ConfigureAwait(false))
        {
            try { File.Delete(partPath); } catch { /* Preserve the integrity error. */ }
            throw new InvalidDataException($"Integrity check failed for {Path.GetFileName(artifact.RelativePath)}.");
        }

        File.Move(partPath, finalPath, overwrite: true);
    }

    private bool ArtifactExists(FunAsrArtifact artifact)
    {
        string path = ResolvePath(artifact.RelativePath);
        return File.Exists(path) && new FileInfo(path).Length == artifact.Size;
    }

    private bool ArtifactIsVerified(FunAsrArtifact artifact) =>
        FileIsVerified(ResolvePath(artifact.RelativePath), artifact.Size, artifact.Sha256);

    private bool RuntimeFilesAreVerified(IReadOnlyCollection<InstalledArtifact>? installed)
    {
        if (installed is null || installed.Count != RequiredExecutables.Length)
            return false;
        foreach (string executable in RequiredExecutables)
        {
            string relativePath = Path.Combine("runtime", FunAsrModelCatalog.RuntimeVersion, executable);
            InstalledArtifact? expected = installed.FirstOrDefault(item =>
                item is not null
                && string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (expected is null
                || !FileIsVerified(ResolvePath(relativePath), expected.Size, expected.Sha256))
            {
                return false;
            }
        }
        return true;
    }

    private bool FileIsVerified(string path, long expectedSize, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path))
            return false;
        var file = new FileInfo(path);
        if (file.Length != expectedSize)
            return false;
        var stamp = new VerifiedFile(file.Length, file.LastWriteTimeUtc.Ticks, expectedSha256);
        lock (_verificationGate)
        {
            if (_verifiedFiles.TryGetValue(path, out VerifiedFile? verified) && verified == stamp)
                return true;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.SequentialScan);
        string actualSha256 = Convert.ToHexString(SHA256.HashData(stream));
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        lock (_verificationGate)
            _verifiedFiles[path] = stamp;
        return true;
    }

    private string GetRuntimePath(string executable) =>
        ResolvePath(Path.Combine("runtime", FunAsrModelCatalog.RuntimeVersion, executable));

    private static bool RuntimeIsComplete(string directory) =>
        RequiredExecutables.All(executable => File.Exists(Path.Combine(directory, executable)));

    private static string? FindRuntimePayload(string stagingPath) =>
        Directory.EnumerateDirectories(stagingPath, "*", SearchOption.AllDirectories)
            .Prepend(stagingPath)
            .FirstOrDefault(RuntimeIsComplete);

    private static void ExtractArchive(string archivePath, string stagingPath)
    {
        string root = Path.GetFullPath(stagingPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string targetPath = Path.GetFullPath(Path.Combine(stagingPath, relativePath));
            if (!targetPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The FunASR runtime archive contains an unsafe path.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: false);
        }
    }

    private static async Task<bool> MatchesAsync(
        string path, FunAsrArtifact artifact, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.Size)
            return false;
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ArtifactsMatch(
        IReadOnlyCollection<InstalledArtifact> installed,
        IReadOnlyCollection<FunAsrArtifact> expected) =>
        installed.Count == expected.Count
        && expected.All(artifact => installed.Any(item => item is not null && item.Matches(artifact)));

    private void MarkInstalled(FunAsrModelDefinition model)
    {
        InstallationManifest manifest = LoadManifest();
        manifest.RuntimeVersion = FunAsrModelCatalog.RuntimeVersion;
        manifest.RuntimeSha256 = _runtimeArtifact.Sha256;
        manifest.RuntimeFiles = RequiredExecutables.Select(executable =>
        {
            string relativePath = Path.Combine("runtime", FunAsrModelCatalog.RuntimeVersion, executable);
            return InstalledArtifact.FromFile(relativePath, ResolvePath(relativePath));
        }).ToList();
        manifest.Vad = InstalledArtifact.From(_vadArtifact);
        manifest.Models ??= new(StringComparer.OrdinalIgnoreCase);
        manifest.Models[model.Id] = model.Artifacts.Select(InstalledArtifact.From).ToList();
        SaveManifest(manifest);
    }

    private InstallationManifest LoadManifest()
    {
        string path = ResolvePath("installation.json");
        if (!File.Exists(path))
            return new();

        try
        {
            InstallationManifest manifest = JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(path))
                ?? new();
            manifest.Models ??= new(StringComparer.OrdinalIgnoreCase);
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    private void SaveManifest(InstallationManifest manifest)
    {
        Directory.CreateDirectory(RootPath);
        string path = ResolvePath("installation.json");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static async Task RunSmokeTestAsync(
        FunAsrResolvedModel resolved, CancellationToken cancellationToken)
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "VoiceInput");
        Directory.CreateDirectory(temporaryDirectory);
        string wavePath = Path.Combine(temporaryDirectory, $"funasr-smoke-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavePath, PcmWave.Wrap(new byte[32_000], 16_000), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            FunAsrProcessResult result = await FunAsrProcess.RunAsync(
                FunAsrProcess.CreateStartInfo(resolved, wavePath), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(result.StandardError)
                    ? string.Empty
                    : $" {result.StandardError.Trim()}";
                throw new InvalidOperationException(
                    $"FunASR runtime smoke test failed with exit code {result.ExitCode}.{detail}");
            }
        }
        finally
        {
            File.Delete(wavePath);
        }
    }

    private void Report(
        string modelId,
        FunAsrInstallStage stage,
        string artifact,
        long downloadedBytes,
        long? totalBytes,
        string? error = null) =>
        ProgressChanged?.Invoke(new(modelId, stage, artifact, downloadedBytes, totalBytes, error));

    private string ResolvePath(string relativePath)
    {
        string root = RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("FunASR artifact path escapes the managed root.");
        return path;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class InstallationManifest
    {
        public string RuntimeVersion { get; set; } = string.Empty;
        public string RuntimeSha256 { get; set; } = string.Empty;
        public List<InstalledArtifact>? RuntimeFiles { get; set; }
        public InstalledArtifact? Vad { get; set; }
        public Dictionary<string, List<InstalledArtifact>>? Models { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record InstalledArtifact(string RelativePath, long Size, string Sha256)
    {
        public static InstalledArtifact From(FunAsrArtifact artifact) =>
            new(artifact.RelativePath, artifact.Size, artifact.Sha256);

        public static InstalledArtifact FromFile(string relativePath, string path)
        {
            var file = new FileInfo(path);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.SequentialScan);
            return new(relativePath, file.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }

        public bool Matches(FunAsrArtifact artifact) =>
            string.Equals(RelativePath, artifact.RelativePath, StringComparison.OrdinalIgnoreCase)
            && Size == artifact.Size
            && string.Equals(Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record VerifiedFile(long Size, long LastWriteTimeUtcTicks, string Sha256);
}
