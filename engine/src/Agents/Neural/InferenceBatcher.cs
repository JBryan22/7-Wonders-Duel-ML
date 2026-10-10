using System.Collections.Concurrent;
using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Agents;

/// <summary>
/// Batched inference (spec A2/A7: "one batching inference thread"): many searches running on their own threads hand
/// their positions to one inference thread, which runs everything waiting as a single batch. Batching is greedy: the
/// thread never waits for a batch to fill, it takes whatever has queued up (at most <see cref="MaxBatch"/>) while the
/// previous batch ran, so a lone search pays no extra latency and many concurrent searches share one network call.
/// <see cref="Evaluate"/> blocks its caller until its position's outputs are back.
/// </summary>
public sealed class InferenceBatcher : IDisposable
{
    public const int DefaultMaxBatch = 512;

    /// <summary>One position waiting for the network; owned and reused by one caller (one search at a time).</summary>
    public sealed class Request
    {
        public readonly float[] Input = new float[NetworkEncoding.InputSize];
        public readonly float[] Policy = new float[NetworkEncoding.PolicySize];
        public readonly float[] Outcome = new float[NetworkEncoding.OutcomeClasses.Length];
        public float Value;
        internal readonly ManualResetEventSlim Done = new(false);
        internal Exception? Error;
    }

    readonly Queue<Request> _queue = new();
    readonly Thread _thread;
    readonly float[] _input, _policy, _value, _outcome;
    readonly List<Request> _batch = new();
    bool _stopping;
    long _batches, _positions;

    public InferenceBatcher(NeuralNetwork network, int maxBatch = DefaultMaxBatch)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBatch, 1);
        Network = network;
        MaxBatch = maxBatch;
        _input = new float[maxBatch * NetworkEncoding.InputSize];
        _policy = new float[maxBatch * NetworkEncoding.PolicySize];
        _value = new float[maxBatch];
        _outcome = new float[maxBatch * NetworkEncoding.OutcomeClasses.Length];
        _thread = new Thread(Loop) { IsBackground = true, Name = "7wd inference" };
        _thread.Start();
    }

    public NeuralNetwork Network { get; }
    public int MaxBatch { get; }

    /// <summary>Network calls made so far.</summary>
    public long Batches => Interlocked.Read(ref _batches);

    /// <summary>Positions evaluated so far (<see cref="Positions"/> / <see cref="Batches"/> is the mean batch size).</summary>
    public long Positions => Interlocked.Read(ref _positions);

    /// <summary>Evaluates the request's <see cref="Request.Input"/>, filling its policy logits, value and outcome logits.</summary>
    public void Evaluate(Request request)
    {
        request.Done.Reset();
        request.Error = null;
        lock (_queue)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _queue.Enqueue(request);
            if (_queue.Count == 1) Monitor.Pulse(_queue);
        }
        request.Done.Wait();
        if (request.Error is { } e) throw new InvalidOperationException($"Inference failed: {e.Message}", e);
    }

    void Loop()
    {
        var inputSize = NetworkEncoding.InputSize;
        var policySize = NetworkEncoding.PolicySize;
        var outcomeSize = NetworkEncoding.OutcomeClasses.Length;
        while (true)
        {
            _batch.Clear();
            lock (_queue)
            {
                while (_queue.Count == 0 && !_stopping) Monitor.Wait(_queue);
                if (_queue.Count == 0) return;
                while (_queue.Count > 0 && _batch.Count < MaxBatch) _batch.Add(_queue.Dequeue());
            }
            var n = _batch.Count;
            try
            {
                for (var i = 0; i < n; i++) _batch[i].Input.CopyTo(_input, i * inputSize);
                Network.Run(_input, n, _policy, _value, _outcome);
                for (var i = 0; i < n; i++)
                {
                    var r = _batch[i];
                    _policy.AsSpan(i * policySize, policySize).CopyTo(r.Policy);
                    _outcome.AsSpan(i * outcomeSize, outcomeSize).CopyTo(r.Outcome);
                    r.Value = _value[i];
                }
            }
            catch (Exception e)
            {
                foreach (var r in _batch) r.Error = e;
            }
            Interlocked.Increment(ref _batches);
            Interlocked.Add(ref _positions, n);
            foreach (var r in _batch) r.Done.Set();
        }
    }

    /// <summary>Stops the inference thread after the queued requests (the network itself is not disposed).</summary>
    public void Dispose()
    {
        lock (_queue)
        {
            _stopping = true;
            Monitor.PulseAll(_queue);
        }
        _thread.Join();
    }
}

/// <summary>
/// The networks loaded in this process, one <see cref="InferenceBatcher"/> per model file and device, shared by every
/// Agent that names it (a Match creates fresh Agents for every game, and all of them should batch together). A file
/// rewritten on disk is loaded again.
/// </summary>
public static class LoadedNetworks
{
    static readonly ConcurrentDictionary<(string Path, DateTime Written, InferenceDevice Device, int Threads), Lazy<InferenceBatcher>> Cache = new();

    /// <summary>Every network loaded so far.</summary>
    public static IEnumerable<InferenceBatcher> All =>
        Cache.Values.Where(l => l.IsValueCreated).Select(l => { try { return l.Value; } catch { return null; } }).OfType<InferenceBatcher>();

    public static InferenceBatcher Get(string path, InferenceDevice device = InferenceDevice.Auto, int cpuThreads = 0)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"No network at {path}", path);
        var key = (full, File.GetLastWriteTimeUtc(full), device, cpuThreads);
        var lazy = Cache.GetOrAdd(key, k => new Lazy<InferenceBatcher>(() => new InferenceBatcher(NeuralNetwork.Load(k.Path, k.Device, k.Threads))));
        try
        {
            return lazy.Value;
        }
        catch
        {
            Cache.TryRemove(new KeyValuePair<(string, DateTime, InferenceDevice, int), Lazy<InferenceBatcher>>(key, lazy));
            throw;
        }
    }
}
