using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Microsoft.Windows.AI.MachineLearning;
using System.Diagnostics;
using System.Text.Json;

var options = ParseOptions(args);
var modelPath = Path.GetFullPath(RequireOption(options, "model"));
var vocabPath = Path.GetFullPath(RequireOption(options, "vocab"));
var text = options.GetValueOrDefault(
    "text",
    "search_document: Glint captures focused window context privately.");

var tokenizer = BertTokenizer.Create(
    vocabPath,
    new BertOptions
    {
        LowerCaseBeforeTokenization = true
    });
var tokenIds = tokenizer.EncodeToIds(
    text,
    addSpecialTokens: true,
    considerNormalization: true,
    considerPreTokenization: true);
if (tokenIds.Count == 0)
{
    throw new InvalidOperationException("Tokenizer produced no input IDs.");
}

var catalogTimer = Stopwatch.StartNew();
var catalog = ExecutionProviderCatalog.GetDefault();
await catalog.EnsureAndRegisterCertifiedAsync();
catalogTimer.Stop();

using var sessionOptions = new SessionOptions
{
    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
};
sessionOptions.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MIN_OVERALL_POWER);

var loadTimer = Stopwatch.StartNew();
using var session = new InferenceSession(modelPath, sessionOptions);
loadTimer.Stop();

var ids = tokenIds.Select(value => (long)value).ToArray();
var attention = Enumerable.Repeat(1L, ids.Length).ToArray();
var tokenTypes = new long[ids.Length];
var shape = new[] { 1, ids.Length };
var availableInputs = session.InputMetadata.Keys.ToHashSet(StringComparer.Ordinal);
var inputs = new List<NamedOnnxValue>();
AddInput(inputs, availableInputs, "input_ids", ids, shape);
AddInput(inputs, availableInputs, "attention_mask", attention, shape);
AddInput(inputs, availableInputs, "token_type_ids", tokenTypes, shape);

var inferenceTimer = Stopwatch.StartNew();
using var results = session.Run(inputs);
inferenceTimer.Stop();

var output = results.FirstOrDefault(result => result.Name == "last_hidden_state")
             ?? results.First();
var hiddenState = output.AsTensor<float>();
var dimensions = hiddenState.Dimensions.ToArray();
if (dimensions.Length != 3 || dimensions[0] != 1 || dimensions[1] != ids.Length)
{
    throw new InvalidDataException(
        $"Unexpected embedding output shape: [{string.Join(',', dimensions)}].");
}

var embedding = MeanPoolAndNormalize(hiddenState, attention, dimensions[2]);
if (embedding.Length != 768)
{
    throw new InvalidDataException(
        $"Expected a 768-dimensional embedding, received {embedding.Length}.");
}

Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        model = Path.GetFileName(modelPath),
        tokens = ids.Length,
        dimensions = embedding.Length,
        l2Norm = Math.Sqrt(embedding.Sum(value => value * value)),
        firstValues = embedding.Take(8),
        catalogMs = catalogTimer.Elapsed.TotalMilliseconds,
        loadMs = loadTimer.Elapsed.TotalMilliseconds,
        inferenceMs = inferenceTimer.Elapsed.TotalMilliseconds,
        inputs = availableInputs,
        output = output.Name
    },
    new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));

static void AddInput(
    ICollection<NamedOnnxValue> inputs,
    IReadOnlySet<string> available,
    string name,
    long[] values,
    int[] shape)
{
    if (available.Contains(name))
    {
        inputs.Add(NamedOnnxValue.CreateFromTensor(
            name,
            new DenseTensor<long>(values, shape)));
    }
}

static float[] MeanPoolAndNormalize(
    Tensor<float> hiddenState,
    long[] attentionMask,
    int dimensions)
{
    var result = new float[dimensions];
    var included = 0;
    for (var token = 0; token < attentionMask.Length; token++)
    {
        if (attentionMask[token] == 0)
        {
            continue;
        }

        included++;
        for (var dimension = 0; dimension < dimensions; dimension++)
        {
            result[dimension] += hiddenState[0, token, dimension];
        }
    }

    if (included == 0)
    {
        throw new InvalidDataException("Attention mask excluded every token.");
    }

    var normSquared = 0d;
    for (var index = 0; index < result.Length; index++)
    {
        result[index] /= included;
        normSquared += result[index] * result[index];
    }

    var norm = Math.Sqrt(normSquared);
    if (norm == 0)
    {
        throw new InvalidDataException("Embedding has zero norm.");
    }

    for (var index = 0; index < result.Length; index++)
    {
        result[index] = (float)(result[index] / norm);
    }

    return result;
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index++)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = values[index][2..];
        result[key] = index + 1 < values.Length
                      && !values[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? values[++index]
            : "true";
    }

    return result;
}

static string RequireOption(IReadOnlyDictionary<string, string> options, string key) =>
    options.GetValueOrDefault(key)
    ?? throw new ArgumentException($"Missing required option --{key}.");
