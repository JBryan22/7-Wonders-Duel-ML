using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>Where a network runs: <see cref="Auto"/> tries CUDA and falls back to the CPU.</summary>
public enum InferenceDevice { Auto, Cpu, Cuda }

/// <summary>
/// An exported network (spec A6/A7) loaded into ONNX Runtime: one input <c>input</c> [batch, input size], outputs
/// <c>policy</c> [batch, policy size] raw unmasked logits, <c>value</c> [batch] in −1..1 and <c>outcome</c> [batch, 7]
/// logits (training/sevenwd/export.py; Encoding v1: 2,417 inputs and 1,509 logits). Loading checks the version
/// handshake: the model's <c>encoding_version</c> must be one this build has (<see cref="EncodingVersion.All"/>: v1, or
/// v2 for Pantheon, spec-pantheon P-A7) and its sizes must match that Encoding, otherwise it is refused. The network
/// then encodes with its own version (<see cref="Encoding"/>).
/// <para>
/// A network may drop input blocks (metadata <c>dropped_inputs</c>, e.g. <c>derived</c>: a pilot trained without the
/// derived features). It zeroes them inside the model, so it takes the same Encoding inputs; loading checks that every
/// name is an input block of this Encoding (<see cref="DroppedInputs"/>).
/// </para>
/// <para>
/// <see cref="Run"/> is thread-safe, but search goes through an <see cref="InferenceBatcher"/>, which batches the
/// requests of many concurrent searches into one call.
/// </para>
/// </summary>
public sealed class NeuralNetwork : IDisposable
{
    public const string Format = "7wd-network";
    public const string InputName = "input";
    static readonly string[] OutputNames = ["policy", "value", "outcome"];

    readonly InferenceSession _session;
    readonly RunOptions _runOptions = new();
    readonly string[] _inputNames = [InputName];

    NeuralNetwork(string path, InferenceSession session, InferenceDevice device, IReadOnlyDictionary<string, string> metadata,
        EncodingVersion encoding)
    {
        Path = path;
        _session = session;
        Device = device;
        Metadata = metadata;
        Encoding = encoding;
    }

    /// <summary>The Encoding version the model declares: what its inputs and policy logits mean.</summary>
    public EncodingVersion Encoding { get; }

    /// <summary>The model file.</summary>
    public string Path { get; }

    /// <summary>Where it actually runs (never <see cref="InferenceDevice.Auto"/>).</summary>
    public InferenceDevice Device { get; }

    /// <summary>The model's metadata as exported (format, encoding_version, sizes, width, blocks, ...).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>The Encoding input blocks the network never sees (zeroed inside the model); empty for most networks.</summary>
    public IReadOnlyList<string> DroppedInputs => ParseDropped(Metadata);

    static string[] ParseDropped(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue("dropped_inputs", out var dropped) && dropped.Length > 0 ? dropped.Split(',') : [];

    /// <summary>Why CUDA was not used when <see cref="InferenceDevice.Auto"/> fell back to the CPU (null otherwise).</summary>
    public string? FallbackReason { get; private init; }

    public override string ToString() =>
        $"{System.IO.Path.GetFileName(Path)} (Encoding v{Metadata.GetValueOrDefault("encoding_version")}, " +
        $"{Metadata.GetValueOrDefault("width")}x{Metadata.GetValueOrDefault("blocks")}, " +
        (DroppedInputs.Count > 0 ? $"without {string.Join(", ", DroppedInputs)}, " : "") +
        $"{Device.ToString().ToUpperInvariant()})";

    /// <summary>
    /// Loads a model and checks it against this build's Encoding. <paramref name="cpuThreads"/> is ONNX Runtime's
    /// intra-op thread count on the CPU (0 = its default, one per core). Throws <see cref="FormatException"/> for a model
    /// of another Encoding version or shape, <see cref="FileNotFoundException"/> when there is no file, and
    /// <see cref="NotSupportedException"/> when <see cref="InferenceDevice.Cuda"/> is asked for but cannot be used.
    /// </summary>
    public static NeuralNetwork Load(string path, InferenceDevice device = InferenceDevice.Auto, int cpuThreads = 0)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"No network at {path}", path);
        string? fallback = null;
        // Auto tries CUDA only on Windows: the Linux build is CPU-only.
        if (device == InferenceDevice.Cuda || device == InferenceDevice.Auto && OperatingSystem.IsWindows())
        {
            try
            {
                return Check(path, Open(path, InferenceDevice.Cuda, cpuThreads), InferenceDevice.Cuda);
            }
            catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                if (device == InferenceDevice.Cuda)
                    throw new NotSupportedException($"CUDA inference is not available: {FirstLine(e.Message)}{CudaLibraries.Hint}", e);
                fallback = FirstLine(e.Message);
            }
        }
        var network = Check(path, Open(path, InferenceDevice.Cpu, cpuThreads), InferenceDevice.Cpu);
        return fallback is null ? network : new NeuralNetwork(network.Path, network._session, network.Device, network.Metadata, network.Encoding) { FallbackReason = fallback };
    }

    static string FirstLine(string s) => s.Split('\n')[0].Trim();

    static InferenceSession Open(string path, InferenceDevice device, int cpuThreads)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = cpuThreads,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        try
        {
            if (device == InferenceDevice.Cuda)
            {
                CudaLibraries.Prepare();
                options.AppendExecutionProvider_CUDA(0);
            }
            return new InferenceSession(path, options);
        }
        finally
        {
            options.Dispose();
        }
    }

    /// <summary>The version handshake and shape checks; disposes the session when the model is refused.</summary>
    static NeuralNetwork Check(string path, InferenceSession session, InferenceDevice device)
    {
        try
        {
            var metadata = session.ModelMetadata.CustomMetadataMap;
            var name = System.IO.Path.GetFileName(path);
            if (metadata.GetValueOrDefault("format") != Format)
                throw new FormatException($"{name} is not a 7wd network (no format={Format} metadata)");
            var version = metadata.GetValueOrDefault("encoding_version");
            var encoding = (int.TryParse(version, out var number) && version == number.ToString() ? EncodingVersion.TryOf(number) : null)
                ?? throw new FormatException(
                    $"{name} was exported for Encoding v{version ?? "?"}, but this build uses " +
                    $"Encoding v{string.Join(" or v", EncodingVersion.All.Select(v => v.Number))}; " +
                    "use a network trained on one of these, or a build of the matching Encoding version");

            if (session.InputMetadata.Count != 1 || !session.InputMetadata.TryGetValue(InputName, out var input)
                || input.Dimensions.Length != 2 || input.Dimensions[1] != encoding.InputSize)
                throw new FormatException($"{name} must take one input '{InputName}' [batch, {encoding.InputSize}] ({encoding})");
            int[] widths = [encoding.PolicySize, 0, NetworkEncoding.OutcomeClasses.Length];
            for (var i = 0; i < OutputNames.Length; i++)
            {
                if (!session.OutputMetadata.TryGetValue(OutputNames[i], out var output)
                    || output.Dimensions.Length != (widths[i] == 0 ? 1 : 2)
                    || widths[i] != 0 && output.Dimensions[1] != widths[i])
                    throw new FormatException($"{name} must have outputs policy [batch, {widths[0]}], value [batch], outcome [batch, {widths[2]}]");
            }
            if (metadata.TryGetValue("outcome_classes", out var classes) && classes != string.Join(',', NetworkEncoding.OutcomeClasses))
                throw new FormatException($"{name}'s outcome classes ({classes}) differ from the Encoding's");
            foreach (var block in ParseDropped(metadata))
                if (!encoding.InputBlocks.Any(b => b.Name == block))
                    throw new FormatException(
                        $"{name} drops the input block '{block}', which {encoding} doesn't have " +
                        $"(blocks: {string.Join(", ", encoding.InputBlocks.Select(b => b.Name))})");
            return new NeuralNetwork(path, session, device, new Dictionary<string, string>(metadata), encoding);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="batch"/> positions: <paramref name="input"/> holds batch × InputSize numbers (of
    /// <see cref="Encoding"/>); the outputs receive batch × PolicySize logits, batch values and batch × outcome-class logits.
    /// </summary>
    public void Run(float[] input, int batch, Span<float> policy, Span<float> value, Span<float> outcome)
    {
        if (batch <= 0) throw new ArgumentOutOfRangeException(nameof(batch));
        var inputSize = Encoding.InputSize;
        using var tensor = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance,
            input.AsMemory(0, batch * inputSize), [batch, inputSize]);
        using var outputs = _session.Run(_runOptions, _inputNames, [tensor], OutputNames);
        outputs[0].GetTensorDataAsSpan<float>()[..(batch * Encoding.PolicySize)].CopyTo(policy);
        outputs[1].GetTensorDataAsSpan<float>()[..batch].CopyTo(value);
        outputs[2].GetTensorDataAsSpan<float>()[..(batch * NetworkEncoding.OutcomeClasses.Length)].CopyTo(outcome);
    }

    public void Dispose()
    {
        _session.Dispose();
        _runOptions.Dispose();
    }
}

/// <summary>
/// Makes the CUDA libraries ONNX Runtime's CUDA provider needs (CUDA 13, cuDNN 9) findable on Windows. Rather than a
/// separate CUDA install, it uses the copies PyTorch's CUDA wheel already put in the training venv
/// (<c>training/.venv/Lib/site-packages/torch/lib</c>, found by searching up from the program and the current
/// directory), or the folder named by the <c>SEVENWD_CUDA_LIBS</c> environment variable. Elsewhere it does nothing (the
/// Linux build is CPU-only).
/// </summary>
static class CudaLibraries
{
    const string EnvironmentVariable = "SEVENWD_CUDA_LIBS";
    static readonly Lock Gate = new();
    static bool _prepared;

    public static string Hint => OperatingSystem.IsWindows()
        ? $" (needs an NVIDIA GPU plus CUDA 13 and cuDNN 9: run 'uv sync' in training/ or set {EnvironmentVariable} to a folder holding them)"
        : " (this build of ONNX Runtime is CPU-only on Linux)";

    /// <summary>The folder found, or null.</summary>
    public static string? Folder { get; private set; }

    public static void Prepare()
    {
        lock (Gate)
        {
            if (_prepared) return;
            _prepared = true;
            if (!OperatingSystem.IsWindows()) return;
            Folder = Find();
            if (Folder is null) return;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", Folder + System.IO.Path.PathSeparator + path);
            // Load the CUDA runtime from there up front, so the provider's own imports resolve to the same copies.
            foreach (var dll in new[] { "cudart64_13.dll", "cublasLt64_13.dll", "cublas64_13.dll", "cudnn64_9.dll" })
                NativeLibrary.TryLoad(System.IO.Path.Combine(Folder, dll), out _);
        }
    }

    static string? Find()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } env && Directory.Exists(env)) return env;
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "training", ".venv", "Lib", "site-packages", "torch", "lib");
                if (File.Exists(System.IO.Path.Combine(candidate, "cudart64_13.dll"))) return candidate;
            }
        }
        return null;
    }
}
