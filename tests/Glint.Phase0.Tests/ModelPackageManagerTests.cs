using Glint.Phase0.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Glint.Phase0.Tests;

public sealed class ModelPackageManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "glint-phase0-model-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ModelLifecycleSupportsResumeRollbackRepairAndRemoval()
    {
        var firstBytes = CreatePayload(8192, seed: 1);
        var secondBytes = CreatePayload(12288, seed: 2);
        var first = CreateManifest("1.0.0", "v1.litertlm", firstBytes);
        var second = CreateManifest("2.0.0", "v2.litertlm", secondBytes);
        var handler = new ArtifactHandler(new Dictionary<string, byte[]>
        {
            [first.DownloadUri.AbsolutePath] = firstBytes,
            [second.DownloadUri.AbsolutePath] = secondBytes
        });
        using var http = new HttpClient(handler);
        var manager = new ModelPackageManager(http, _directory);

        var partial = Path.Combine(
            _directory,
            first.ModelId,
            first.Version,
            first.FileName + ".partial");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, firstBytes[..1024]);

        var firstPath = await manager.InstallAsync(first);
        Assert.Equal(1024, handler.RequestedRanges[first.DownloadUri.AbsolutePath]);
        Assert.True(await manager.VerifyFileAsync(firstPath, first));

        var secondPath = await manager.InstallAsync(second);
        var active = manager.GetActiveModel();
        Assert.NotNull(active);
        Assert.Equal(secondPath, active.Path);
        Assert.Equal(firstPath, active.PreviousPath);

        Assert.True(manager.Rollback());
        Assert.Equal(firstPath, manager.GetActiveModelPath());
        Assert.True(manager.Rollback());
        Assert.Equal(secondPath, manager.GetActiveModelPath());

        manager.ConfirmActiveVersion();
        Assert.Null(manager.GetActiveModel()!.PreviousPath);
        Assert.True(manager.RemoveVersion(first.ModelId, first.Version));
        Assert.False(File.Exists(firstPath));

        await File.WriteAllTextAsync(secondPath, "corrupt");
        var repaired = await manager.RepairAsync(second);
        Assert.Equal(secondPath, repaired);
        Assert.True(await manager.VerifyFileAsync(secondPath, second));

        Assert.True(manager.RemoveActiveModel());
        Assert.Null(manager.GetActiveModel());
        Assert.False(File.Exists(secondPath));
    }

    [Fact]
    public async Task InvalidHashNeverActivatesModel()
    {
        var bytes = CreatePayload(1024, seed: 7);
        var manifest = CreateManifest("bad", "bad.litertlm", bytes) with
        {
            Sha256 = new string('0', 64)
        };
        using var http = new HttpClient(new ArtifactHandler(
            new Dictionary<string, byte[]>
            {
                [manifest.DownloadUri.AbsolutePath] = bytes
            }));
        var manager = new ModelPackageManager(http, _directory);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.InstallAsync(manifest));
        Assert.Null(manager.GetActiveModel());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ModelArtifactManifest CreateManifest(
        string version,
        string fileName,
        byte[] bytes) =>
        new(
            "gemma-4-e2b",
            version,
            new Uri($"https://models.glint.test/{fileName}"),
            bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)),
            "litert-lm-0.12.0",
            "Apache-2.0",
            fileName);

    private static byte[] CreatePayload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class ArtifactHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, byte[]> _artifacts;

        public ArtifactHandler(IReadOnlyDictionary<string, byte[]> artifacts)
        {
            _artifacts = artifacts;
        }

        public Dictionary<string, long?> RequestedRanges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var bytes = _artifacts[path];
            var from = request.Headers.Range?.Ranges.Single().From;
            RequestedRanges[path] = from;
            var offset = checked((int)(from ?? 0));
            var response = new HttpResponseMessage(
                from.HasValue ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[offset..])
            };
            if (from.HasValue)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                    offset,
                    bytes.Length - 1,
                    bytes.Length);
            }

            return Task.FromResult(response);
        }
    }
}
