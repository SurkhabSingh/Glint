using System.Text.Json;

namespace Glint.Phase0.Core;

public sealed record LiteRtRuntimeResolution(
    string? PythonExecutable,
    string? WorkerScript,
    string? ModelPath,
    string ModelId,
    IReadOnlyList<string> Missing)
{
    public bool IsReady => Missing.Count == 0;
}

public static class LiteRtRuntimeLocator
{
    public static LiteRtRuntimeResolution Resolve(
        string baseDirectory,
        string dataRoot)
    {
        var roots = Ancestors(baseDirectory).ToArray();
        var python = FirstExisting(
            Environment.GetEnvironmentVariable("GLINT_LITERT_PYTHON"),
            Path.Combine(dataRoot, "runtime", "python", "python.exe"),
            roots.Select(root =>
                Path.Combine(root, "tools", "litert", ".venv", "Scripts", "python.exe")));
        var worker = FirstExisting(
            Environment.GetEnvironmentVariable("GLINT_LITERT_WORKER"),
            Path.Combine(baseDirectory, "tools", "litert", "worker.py"),
            roots.Select(root => Path.Combine(root, "tools", "litert", "worker.py")));
        var model = FirstExisting(
            Environment.GetEnvironmentVariable("GLINT_GEMMA_MODEL"),
            ReadActiveModelPath(dataRoot),
            roots.Select(root =>
                Path.Combine(
                    root,
                    "models",
                    "upstream",
                    "gemma-4-e2b",
                    "gemma-4-E2B-it.litertlm")));

        var missing = new List<string>();
        if (python is null)
        {
            missing.Add("LiteRT Python runtime");
        }
        if (worker is null)
        {
            missing.Add("LiteRT worker");
        }
        if (model is null)
        {
            missing.Add("Gemma 4 E2B model");
        }

        return new(python, worker, model, "gemma-4-e2b", missing);
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path));
             directory is not null;
             directory = directory.Parent)
        {
            yield return directory.FullName;
        }
    }

    private static string? FirstExisting(
        string? first,
        string? second,
        IEnumerable<string> remaining)
    {
        foreach (var candidate in new[] { first, second }.Concat(remaining))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string? ReadActiveModelPath(string dataRoot)
    {
        var pointer = Path.Combine(dataRoot, "models", "active.json");
        if (!File.Exists(pointer))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(pointer));
            return document.RootElement.TryGetProperty("path", out var path)
                ? path.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
