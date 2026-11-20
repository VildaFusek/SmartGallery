using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using SmartGallery.AI.Models;
using SmartGallery.Application.AI;

namespace SmartGallery.AI.Florence;

public sealed class FlorenceCpuRuntimeValidator(
    ILocalCaptionModelInspector inspector, CaptionModelVerificationCache cache) : ICaptionRuntimeValidator
{
    public Task<CaptionRuntimeValidation> ValidateAsync(
        IProgress<CaptionRuntimeProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => ValidateCoreAsync(progress, cancellationToken), cancellationToken);

    private async Task<CaptionRuntimeValidation> ValidateCoreAsync(
        IProgress<CaptionRuntimeProgress> progress, CancellationToken cancellationToken)
    {
        using var resource = typeof(FlorenceCpuRuntimeValidator).Assembly
            .GetManifestResourceStream("SmartGallery.AI.ModelManifest.json")
            ?? throw new InvalidOperationException("Chybí manifest Florence-2.");
        var manifest = await JsonSerializer.DeserializeAsync(
            resource, CaptionModelManifestJsonContext.Default.CaptionModelManifest, cancellationToken)
            ?? throw new InvalidDataException("Prázdný manifest Florence-2.");
        if (manifest.SchemaVersion != 1 || manifest.ModelId != "florence-2-base-ft-fp32" ||
            manifest.Revision != "e88a44eaf3791a35eae0c5a47b3dbcd36e67eb6f" || manifest.Files is null)
        {
            throw new InvalidDataException("Nepodporovaná revize Florence-2.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var environment = OrtEnv.Instance();
        environment.DisableTelemetryEvents();
        var version = environment.GetVersionString();
        var cached = await cache.GetRuntimeAsync(version, cancellationToken);
        if (cached is not null)
        {
            progress.Report(new CaptionRuntimeProgress("Dříve ověřené grafy – beze změny", 4, 4));
            return cached with { ReusedVerification = true };
        }

        var contracts = CreateContracts();
        var results = new List<CaptionGraphSignature>();
        foreach (var contract in contracts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new CaptionRuntimeProgress(contract.FileName, results.Count, contracts.Count));
            var relativePath = "onnx/" + contract.FileName;
            var file = manifest.Files.SingleOrDefault(candidate => candidate.Path == relativePath)
                ?? throw new InvalidDataException($"V manifestu chybí {relativePath}.");
            var path = Path.Combine(inspector.ModelDirectory, "onnx", contract.FileName);

            // Keep a read-only handle that denies writes/deletion until the session has been disposed.
            await using var guard = await OpenVerifiedGraphAsync(path, file, cache, cancellationToken);
            using var options = CreateSessionOptions();

            // Session construction is synchronous native work. Cancellation is checked before and after it.
            using var session = new InferenceSession(path, options);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateMetadata(contract.FileName, "vstup", session.InputMetadata, contract.Inputs);
            ValidateMetadata(contract.FileName, "výstup", session.OutputMetadata, contract.Outputs);
            var signature = new CaptionGraphSignature(
                contract.FileName, ReadSignatures(session.InputMetadata), ReadSignatures(session.OutputMetadata));
            results.Add(signature);
            progress.Report(new CaptionRuntimeProgress(contract.FileName, results.Count, contracts.Count));
            // Only one native session is held at a time; no caption inference runs in this step.
        }
        var validation = new CaptionRuntimeValidation(version, results.AsReadOnly());
        await cache.RememberRuntimeAsync(validation, cancellationToken);
        return validation;
    }

    internal static SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
            EnableProfiling = false,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING
        };
        options.AppendExecutionProvider_CPU(useArena: 0);
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return options;
    }

    internal static async Task<FileStream> OpenVerifiedGraphAsync(
        string path, CaptionModelFile expected, CaptionModelVerificationCache cache,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new IOException("ONNX graf není běžný soubor.");
        }
        for (DirectoryInfo? parent = new(Path.GetDirectoryName(path)!);
             parent is not null; parent = parent.Parent)
        {
            if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Cesta ONNX grafu obsahuje odkaz.");
            }
        }
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        try
        {
            if (stream.Length != expected.Bytes ||
                (!await cache.IsCurrentAsync(expected, cancellationToken) &&
                 !CryptographicOperations.FixedTimeEquals(
                    await SHA256.HashDataAsync(stream, cancellationToken), Convert.FromHexString(expected.Sha256))))
            {
                throw new InvalidDataException($"Integrita {expected.Path} se změnila; graf se nenačte.");
            }
            await cache.RememberAsync(expected, cancellationToken);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static IReadOnlyList<CaptionTensorSignature> ReadSignatures(
        IReadOnlyDictionary<string, NodeMetadata> metadata) =>
        metadata.Select(pair => new CaptionTensorSignature(
            pair.Key, pair.Value.ElementType.Name,
            Array.AsReadOnly(pair.Value.Dimensions.ToArray()))).ToList().AsReadOnly();

    private static void ValidateMetadata(
        string graph, string direction,
        IReadOnlyDictionary<string, NodeMetadata> actual, IReadOnlyList<TensorContract> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"{graph}: neočekávaný počet položek ({direction}): {actual.Count} místo {expected.Count}.");
        }
        foreach (var tensor in expected)
        {
            if (!actual.TryGetValue(tensor.Name, out var metadata) || !metadata.IsTensor ||
                metadata.ElementType != tensor.ElementType || metadata.Dimensions.Length != tensor.Dimensions.Count)
            {
                throw new InvalidDataException($"{graph}: neplatný {direction} {tensor.Name}.");
            }
            for (var index = 0; index < tensor.Dimensions.Count; index++)
            {
                if (tensor.Dimensions[index] >= 0 && metadata.Dimensions[index] != tensor.Dimensions[index])
                {
                    throw new InvalidDataException(
                        $"{graph}: {tensor.Name}, rozměr {index} má {metadata.Dimensions[index]} místo {tensor.Dimensions[index]}.");
                }
            }
        }
    }

    private static IReadOnlyList<GraphContract> CreateContracts()
    {
        var decoderInputs = new List<TensorContract>
        {
            new("encoder_attention_mask", typeof(long), [-1, -1]),
            new("encoder_hidden_states", typeof(float), [-1, -1, 768]),
            new("inputs_embeds", typeof(float), [-1, -1, 768])
        };
        var decoderOutputs = new List<TensorContract>
        {
            new("logits", typeof(float), [-1, -1, 51289])
        };
        for (var layer = 0; layer < 6; layer++)
        {
            foreach (var attention in new[] { "decoder", "encoder" })
            {
                foreach (var state in new[] { "key", "value" })
                {
                    decoderInputs.Add(new TensorContract(
                        $"past_key_values.{layer}.{attention}.{state}", typeof(float), [-1, 12, -1, 64]));
                    decoderOutputs.Add(new TensorContract(
                        $"present.{layer}.{attention}.{state}", typeof(float), [-1, 12, -1, 64]));
                }
            }
        }
        decoderInputs.Add(new TensorContract("use_cache_branch", typeof(bool), [1]));
        return
        [
            new GraphContract("vision_encoder.onnx",
                [new("pixel_values", typeof(float), [-1, 3, -1, -1])],
                [new("image_features", typeof(float), [-1, -1, 768])]),
            new GraphContract("embed_tokens.onnx",
                [new("input_ids", typeof(long), [-1, -1])],
                [new("inputs_embeds", typeof(float), [-1, -1, 768])]),
            new GraphContract("encoder_model.onnx",
                [new("attention_mask", typeof(long), [-1, -1]), new("inputs_embeds", typeof(float), [-1, -1, 768])],
                [new("last_hidden_state", typeof(float), [-1, -1, 768])]),
            new GraphContract("decoder_model_merged.onnx", decoderInputs, decoderOutputs)
        ];
    }

    private sealed record TensorContract(string Name, Type ElementType, IReadOnlyList<int> Dimensions);

    private sealed record GraphContract(
        string FileName, IReadOnlyList<TensorContract> Inputs, IReadOnlyList<TensorContract> Outputs);
}
