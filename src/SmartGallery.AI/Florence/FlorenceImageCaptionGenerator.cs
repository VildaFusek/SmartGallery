using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SmartGallery.AI.Models;
using SmartGallery.Application.AI;

namespace SmartGallery.AI.Florence;

public sealed class FlorenceImageCaptionGenerator(
    ILocalCaptionModelInspector inspector,
    CaptionModelVerificationCache cache,
    ICaptionImagePreprocessor images) : IImageCaptionGenerator, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, InferenceSession>? _sessions;
    private FlorenceByteBpeTokenizer? _tokenizer;
    private bool _disposed;

    public Task<ImageCaption> GenerateAsync(string imagePath, CancellationToken cancellationToken) =>
        Task.Run(() => GenerateCoreAsync(imagePath, cancellationToken), cancellationToken);

    private async Task<ImageCaption> GenerateCoreAsync(string imagePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureLoadedAsync(cancellationToken);
            var image = await images.PrepareAsync(imagePath, cancellationToken);
            if (image.Width != 768 || image.Height != 768 || image.Pixels.Length != 3 * 768 * 768)
            {
                throw new InvalidDataException("Neplatný vstupní obraz pro Florence-2.");
            }
            var vision = Run(_sessions!["vision_encoder.onnx"],
                [FloatInput("pixel_values", image.Pixels, [1, 3, 768, 768])],
                ["image_features"], cancellationToken)["image_features"];
            if (vision.Shape.Length != 3 || vision.Shape[0] != 1 || vision.Shape[1] != 577 || vision.Shape[2] != 768)
            {
                throw new InvalidDataException("Vision encoder vrátil neočekávané obrazové tokeny.");
            }

            var prompt = _tokenizer!.EncodeCaptionPrompt();
            var text = Embed(prompt, cancellationToken);
            var sequenceLength = vision.Shape[1] + prompt.Length;
            if (sequenceLength > 1024)
            {
                throw new InvalidDataException("Vstupní sekvence Florence-2 je příliš dlouhá.");
            }
            var embeddings = new float[sequenceLength * 768];
            vision.Data.CopyTo(embeddings, 0);
            text.Data.CopyTo(embeddings, vision.Data.Length);
            var mask = Enumerable.Repeat(1L, sequenceLength).ToArray();
            var encoded = Run(_sessions["encoder_model.onnx"],
                [LongInput("attention_mask", mask, [1, sequenceLength]),
                 FloatInput("inputs_embeds", embeddings, [1, sequenceLength, 768])],
                ["last_hidden_state"], cancellationToken)["last_hidden_state"];

            var generated = new List<long> { 2 }; // decoder_start_token_id
            var past = new Dictionary<string, FloatTensor>(StringComparer.Ordinal);
            var ended = false;
            for (var step = 0; step < 64; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decoderEmbedding = Embed([generated[^1]], cancellationToken);
                var inputs = new List<NamedOnnxValue>
                {
                    LongInput("encoder_attention_mask", mask, [1, sequenceLength]),
                    FloatInput("encoder_hidden_states", encoded.Data, encoded.Shape),
                    FloatInput("inputs_embeds", decoderEmbedding.Data, decoderEmbedding.Shape),
                    NamedOnnxValue.CreateFromTensor("use_cache_branch",
                        new DenseTensor<bool>(new[] { step > 0 }, new[] { 1 }))
                };
                for (var layer = 0; layer < 6; layer++)
                {
                    foreach (var attention in new[] { "decoder", "encoder" })
                    {
                        foreach (var state in new[] { "key", "value" })
                        {
                            var name = $"past_key_values.{layer}.{attention}.{state}";
                            var value = past.TryGetValue(name, out var stored)
                                ? stored : new FloatTensor([], [1, 12, 0, 64]);
                            inputs.Add(FloatInput(name, value.Data, value.Shape));
                        }
                    }
                }
                var outputNames = new List<string> { "logits" };
                for (var layer = 0; layer < 6; layer++)
                {
                    outputNames.Add($"present.{layer}.decoder.key");
                    outputNames.Add($"present.{layer}.decoder.value");
                    if (step == 0)
                    {
                        outputNames.Add($"present.{layer}.encoder.key");
                        outputNames.Add($"present.{layer}.encoder.value");
                    }
                }
                var outputs = Run(_sessions["decoder_model_merged.onnx"], inputs, outputNames, cancellationToken);
                var logits = outputs["logits"].Data;
                if (logits.Length != 51289)
                {
                    throw new InvalidDataException("Decoder vrátil neplatnou velikost slovníku.");
                }
                var next = step == 0 ? 0 : SelectToken(logits, generated);
                generated.Add(next);
                if (next == 2)
                {
                    ended = true;
                    break;
                }
                for (var layer = 0; layer < 6; layer++)
                {
                    foreach (var attention in new[] { "decoder", "encoder" })
                    {
                        if (attention == "encoder" && step > 0)
                        {
                            continue; // The cached branch may emit empty cross-attention outputs.
                        }
                        foreach (var state in new[] { "key", "value" })
                        {
                            past[$"past_key_values.{layer}.{attention}.{state}"] =
                                outputs[$"present.{layer}.{attention}.{state}"];
                        }
                    }
                }
            }
            var caption = _tokenizer.Decode(generated);
            if (string.IsNullOrWhiteSpace(caption))
            {
                throw new InvalidDataException("Model nevygeneroval neprázdný popisek.");
            }
            return new ImageCaption(caption, !ended);
        }
        finally { _gate.Release(); }
    }

    private FloatTensor Embed(long[] ids, CancellationToken cancellationToken) =>
        Run(_sessions!["embed_tokens.onnx"],
            [LongInput("input_ids", ids, [1, ids.Length])], ["inputs_embeds"], cancellationToken)["inputs_embeds"];

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_sessions is not null)
        {
            return;
        }
        var environment = OrtEnv.Instance();
        environment.DisableTelemetryEvents();
        if (await cache.GetRuntimeAsync(environment.GetVersionString(), cancellationToken) is null)
        {
            throw new InvalidOperationException("Nejprve dokonči ověření modelu a ONNX grafů ve spodní liště.");
        }
        using var resource = typeof(FlorenceImageCaptionGenerator).Assembly
            .GetManifestResourceStream("SmartGallery.AI.ModelManifest.json")
            ?? throw new InvalidOperationException("Chybí manifest.");
        var manifest = await JsonSerializer.DeserializeAsync(
            resource, CaptionModelManifestJsonContext.Default.CaptionModelManifest, cancellationToken)
            ?? throw new InvalidDataException("Prázdný manifest.");
        var sessions = new Dictionary<string, InferenceSession>(StringComparer.Ordinal);
        try
        {
            foreach (var name in new[] { "vision_encoder.onnx", "embed_tokens.onnx", "encoder_model.onnx", "decoder_model_merged.onnx" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var spec = manifest.Files.Single(file => file.Path == "onnx/" + name);
                var path = Path.Combine(inspector.ModelDirectory, "onnx", name);
                await using var guard = await FlorenceCpuRuntimeValidator.OpenVerifiedGraphAsync(
                    path, spec, cache, cancellationToken);
                using var options = FlorenceCpuRuntimeValidator.CreateSessionOptions();
                sessions.Add(name, new InferenceSession(path, options));
                cancellationToken.ThrowIfCancellationRequested();
            }
            var tokenizerSpec = manifest.Files.Single(file => file.Path == "tokenizer.json");
            await using var tokenizerStream = await FlorenceCpuRuntimeValidator.OpenVerifiedGraphAsync(
                Path.Combine(inspector.ModelDirectory, "tokenizer.json"), tokenizerSpec, cache, cancellationToken);
            tokenizerStream.Position = 0;
            var tokenizer = new FlorenceByteBpeTokenizer(tokenizerStream);
            _sessions = sessions;
            _tokenizer = tokenizer;
        }
        catch
        {
            foreach (var session in sessions.Values)
            {
                session.Dispose();
            }
            throw;
        }
    }

    private static NamedOnnxValue FloatInput(string name, float[] data, int[] shape) =>
        NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, shape));

    private static NamedOnnxValue LongInput(string name, long[] data, int[] shape) =>
        NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, shape));

    private static Dictionary<string, FloatTensor> Run(
        InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs,
        IReadOnlyCollection<string> outputs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var cancellation = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            using var values = session.Run(inputs, outputs, options);
            cancellationToken.ThrowIfCancellationRequested();
            return values.ToDictionary(
                value => value.Name,
                value =>
                {
                    var tensor = value.AsTensor<float>();
                    return new FloatTensor(tensor.ToArray(), tensor.Dimensions.ToArray());
                }, StringComparer.Ordinal);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static int SelectToken(float[] logits, IReadOnlyList<long> generated)
    {
        var banned = new HashSet<long> { 0, 1 }; // BOS/PAD are not normal continuation tokens.
        if (generated.Count >= 2)
        {
            for (var index = 0; index + 2 < generated.Count; index++)
            {
                if (generated[index] == generated[^2] && generated[index + 1] == generated[^1])
                {
                    banned.Add(generated[index + 2]); // no_repeat_ngram_size = 3
                }
            }
        }
        var best = -1;
        var score = float.NegativeInfinity;
        for (var token = 0; token < logits.Length; token++)
        {
            if (!banned.Contains(token) && float.IsFinite(logits[token]) && logits[token] > score)
            {
                best = token;
                score = logits[token];
            }
        }
        return best >= 0 ? best : throw new InvalidDataException("Decoder vrátil neplatné logits.");
    }

    public void Dispose()
    {
        _disposed = true;
        if (_sessions is not null)
        {
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
        }
        _gate.Dispose();
    }

    private sealed record FloatTensor(float[] Data, int[] Shape);
}
