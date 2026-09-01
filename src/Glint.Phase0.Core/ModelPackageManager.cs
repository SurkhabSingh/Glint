using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glint.Phase0.Core;

public sealed record ModelArtifactManifest(
    string ModelId,
    string Version,
    Uri DownloadUri,
    long SizeBytes,
    string Sha256,
    string RuntimeVersion,
    string License,
    string FileName);

public sealed record ModelInstallProgress(
    long DownloadedBytes,
    long TotalBytes,
    double Fraction);

public sealed record ActiveModelInfo(
    string ModelId,
    string Version,
    string Path,
    string? PreviousModelId,
    string? PreviousVersion,
    string? PreviousPath,
    DateTimeOffset ActivatedAt);

public sealed class ModelPackageManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _modelsRoot;

    public ModelPackageManager(HttpClient httpClient, string modelsRoot)
    {
        _httpClient = httpClient;
        _modelsRoot = Path.GetFullPath(modelsRoot);
    }

    public async Task<string> InstallAsync(
        ModelArtifactManifest manifest,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var versionDirectory = Path.Combine(
            _modelsRoot,
            Sanitize(manifest.ModelId),
            Sanitize(manifest.Version));
        Directory.CreateDirectory(versionDirectory);

        var destination = Path.Combine(versionDirectory, manifest.FileName);
        if (File.Exists(destination)
            && await VerifyFileAsync(destination, manifest, cancellationToken).ConfigureAwait(false))
        {
            Activate(manifest, destination);
            return destination;
        }

        var partial = destination + ".partial";
        var existingLength = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.DownloadUri);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            File.Delete(partial);
            existingLength = 0;
        }

        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(
                         partial,
                         existingLength == 0 ? FileMode.Create : FileMode.Append,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            var downloaded = existingLength;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                progress?.Report(new(
                    downloaded,
                    manifest.SizeBytes,
                    manifest.SizeBytes == 0 ? 0 : Math.Min(1, (double)downloaded / manifest.SizeBytes)));
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Flush(flushToDisk: true);
        }

        if (!await VerifyFileAsync(partial, manifest, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Downloaded model failed size or SHA-256 verification.");
        }

        File.Move(partial, destination, overwrite: true);
        Activate(manifest, destination);
        return destination;
    }

    public async Task<bool> VerifyFileAsync(
        string path,
        ModelArtifactManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != manifest.SizeBytes)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            digest,
            Convert.FromHexString(manifest.Sha256));
    }

    public string? GetActiveModelPath()
    {
        return GetActiveModel()?.Path;
    }

    public ActiveModelInfo? GetActiveModel()
    {
        var pointer = Path.Combine(_modelsRoot, "active.json");
        if (!File.Exists(pointer))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ActiveModelInfo>(
            File.ReadAllText(pointer),
            JsonOptions);
    }

    public async Task<string> RepairAsync(
        ModelArtifactManifest manifest,
        IProgress<ModelInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var active = GetActiveModel();
        if (active is null
            || !string.Equals(active.ModelId, manifest.ModelId, StringComparison.Ordinal)
            || !string.Equals(active.Version, manifest.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The repair manifest does not match the active model.");
        }

        if (await VerifyFileAsync(active.Path, manifest, cancellationToken).ConfigureAwait(false))
        {
            return active.Path;
        }

        return await InstallAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
    }

    public bool Rollback()
    {
        var active = GetActiveModel();
        if (active?.PreviousPath is null
            || active.PreviousModelId is null
            || active.PreviousVersion is null
            || !File.Exists(active.PreviousPath))
        {
            return false;
        }

        WriteActiveModel(new(
            active.PreviousModelId,
            active.PreviousVersion,
            active.PreviousPath,
            active.ModelId,
            active.Version,
            active.Path,
            DateTimeOffset.UtcNow));
        return true;
    }

    public void ConfirmActiveVersion()
    {
        var active = GetActiveModel()
            ?? throw new InvalidOperationException("There is no active model to confirm.");
        WriteActiveModel(active with
        {
            PreviousModelId = null,
            PreviousVersion = null,
            PreviousPath = null
        });
    }

    public bool RemoveVersion(string modelId, string version)
    {
        var versionDirectory = GetVersionDirectory(modelId, version);
        if (!Directory.Exists(versionDirectory))
        {
            return false;
        }

        var active = GetActiveModel();
        if (IsWithinDirectory(active?.Path, versionDirectory)
            || IsWithinDirectory(active?.PreviousPath, versionDirectory))
        {
            throw new InvalidOperationException(
                "Cannot remove the active or rollback model version.");
        }

        Directory.Delete(versionDirectory, recursive: true);
        return true;
    }

    public bool RemoveActiveModel()
    {
        var active = GetActiveModel();
        if (active is null)
        {
            return false;
        }

        var pointer = Path.Combine(_modelsRoot, "active.json");
        File.Delete(pointer);
        var versionDirectory = GetVersionDirectory(active.ModelId, active.Version);
        if (Directory.Exists(versionDirectory))
        {
            Directory.Delete(versionDirectory, recursive: true);
        }

        return true;
    }

    private void Activate(ModelArtifactManifest manifest, string path)
    {
        var previous = GetActiveModel();
        var sameVersion = previous is not null
                          && string.Equals(previous.Path, path, StringComparison.OrdinalIgnoreCase);
        var active = new ActiveModelInfo(
            manifest.ModelId,
            manifest.Version,
            path,
            sameVersion ? previous!.PreviousModelId : previous?.ModelId,
            sameVersion ? previous!.PreviousVersion : previous?.Version,
            sameVersion ? previous!.PreviousPath : previous?.Path,
            DateTimeOffset.UtcNow);
        WriteActiveModel(active);
    }

    private static void ValidateManifest(ModelArtifactManifest manifest)
    {
        if (!manifest.DownloadUri.IsAbsoluteUri || manifest.DownloadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Model downloads must use an absolute HTTPS URI.", nameof(manifest));
        }

        if (manifest.SizeBytes <= 0
            || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit)
            || Path.GetFileName(manifest.FileName) != manifest.FileName)
        {
            throw new ArgumentException("Model manifest fields are invalid.", nameof(manifest));
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private string GetVersionDirectory(string modelId, string version)
    {
        var path = Path.GetFullPath(Path.Combine(
            _modelsRoot,
            Sanitize(modelId),
            Sanitize(version)));
        var relative = Path.GetRelativePath(_modelsRoot, path);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Model path escaped the configured model root.");
        }

        return path;
    }

    private void WriteActiveModel(ActiveModelInfo active)
    {
        Directory.CreateDirectory(_modelsRoot);
        var pointer = Path.Combine(_modelsRoot, "active.json");
        var temporary = pointer + "." + Guid.NewGuid().ToString("N") + ".partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(active, JsonOptions));
        File.Move(temporary, pointer, overwrite: true);
    }

    private static bool IsWithinDirectory(string? path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var relative = Path.GetRelativePath(directory, Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }
}
