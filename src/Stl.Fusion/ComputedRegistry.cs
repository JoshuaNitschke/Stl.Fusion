using Stl.Concurrency;
using Stl.Locking;
using Stl.OS;
using Stl.Time.Internal;
using Errors = Stl.Fusion.Internal.Errors;

namespace Stl.Fusion;

public sealed class ComputedRegistry : IDisposable
{
    public static ComputedRegistry Instance { get; set; } = new();

    public sealed record Options
    {
        internal static readonly PrimeSieve CapacityPrimeSieve;

        public static int DefaultInitialCapacity { get; }
        public static int DefaultInitialConcurrency { get; }
        public static Options Default { get; }

        public int InitialCapacity { get; init; } = DefaultInitialCapacity;
        public int ConcurrencyLevel { get; init; } = DefaultInitialConcurrency;
        public Func<IFunction, IAsyncLockSet<ComputedInput>>? LocksProvider { get; init; } = null;
        public GCHandlePool? GCHandlePool { get; init; } = null;

        static Options()
        {
            DefaultInitialConcurrency = HardwareInfo.GetProcessorCountPo2Factor(16);
            var capacity = HardwareInfo.GetProcessorCountPo2Factor(128, 128);
            CapacityPrimeSieve = new PrimeSieve(capacity + 1024);
            while (!CapacityPrimeSieve.IsPrime(capacity))
                capacity--;
            DefaultInitialCapacity = capacity;
            Default = new();
        }
    }

    private readonly ConcurrentDictionary<ComputedInput, GCHandle> _storage;
    private readonly Func<IFunction, IAsyncLockSet<ComputedInput>> _locksProvider;
    private readonly GCHandlePool _gcHandlePool;
    private readonly StochasticCounter _opCounter;
    private volatile int _pruneCounterThreshold;
    private Task? _pruneTask;
    private object Lock => _storage;

    public ComputedRegistry() : this(Options.Default) { }
    public ComputedRegistry(Options options)
    {
        _storage = new ConcurrentDictionary<ComputedInput, GCHandle>(options.ConcurrencyLevel, options.InitialCapacity);
        var locksProvider = options.LocksProvider;
        if (locksProvider == null) {
            var locks = new AsyncLockSet<ComputedInput>(ReentryMode.CheckedFail);
            locksProvider = _ => locks;
        }
        _locksProvider = locksProvider;
        _gcHandlePool = options.GCHandlePool ?? new GCHandlePool(GCHandleType.Weak);
        if (_gcHandlePool.HandleType != GCHandleType.Weak)
            throw new ArgumentOutOfRangeException(
                $"{nameof(options)}.{nameof(options.GCHandlePool)}.{nameof(_gcHandlePool.HandleType)}");
        _opCounter = new StochasticCounter();
        UpdatePruneCounterThreshold();
    }

    public void Dispose()
        => _gcHandlePool.Dispose();

    public IComputed? Get(ComputedInput key)
    {
        var random = Randomize(key.HashCode);
        OnOperation(random);
        if (_storage.TryGetValue(key, out var handle)) {
            var value = (IComputed?) handle.Target;
            if (value != null)
                return value;
            if (_storage.TryRemove(key, handle))
                _gcHandlePool.Release(handle, random);
        }
        return null;
    }

    public void Register(IComputed computed)
    {
        // Debug.WriteLine($"{nameof(Register)}: {computed}");
        var key = computed.Input;
        var random = Randomize(key.HashCode);
        OnOperation(random);

        var spinWait = new SpinWait();
        GCHandle? newHandle = null;
        while (computed.ConsistencyState != ConsistencyState.Invalidated) {
            if (_storage.TryGetValue(key, out var handle)) {
                var target = (IComputed?) handle.Target;
                if (target == computed)
                    break;
                if (target == null || target.ConsistencyState == ConsistencyState.Invalidated) {
                    if (_storage.TryRemove(key, handle))
                        _gcHandlePool.Release(handle, random);
                }
                else {
                    // This typically triggers Unregister -
                    // except for ReplicaClientComputed.
                    target.Invalidate();
                }
            }
            else {
                newHandle ??= _gcHandlePool.Acquire(computed, random);
                if (_storage.TryAdd(key, newHandle.GetValueOrDefault())) {
                    if (computed.ConsistencyState == ConsistencyState.Invalidated) {
                        if (_storage.TryRemove(key, handle))
                            _gcHandlePool.Release(handle, random);
                    }
                    break;
                }
            }
            spinWait.SpinOnce();
        }
    }

    public bool Unregister(IComputed computed)
    {
        // Debug.WriteLine($"{nameof(Unregister)}: {computed}");
        // We can't remove what still could be invalidated,
        // since "usedBy" links are resolved via this registry
        if (computed.ConsistencyState != ConsistencyState.Invalidated)
            throw Errors.WrongComputedState(computed.ConsistencyState);

        var key = computed.Input;
        var random = Randomize(key.HashCode);
        OnOperation(random);
        if (!_storage.TryGetValue(key, out var handle))
            return false;
        var target = handle.Target;
        if (target != null && !ReferenceEquals(target, computed))
            return false;
        // gcHandle.Target == null (is gone, i.e. to be pruned)
        // or pointing to the right computation object
        if (!_storage.TryRemove(key, handle))
            // If another thread removed the entry, it also released the handle
            return false;
        _gcHandlePool.Release(handle, random);
        return true;
    }

    public IAsyncLockSet<ComputedInput> GetLocksFor(IFunction function)
        => _locksProvider(function);

    public void InvalidateEverything()
    {
        var keys = _storage.Keys.ToList();
        foreach (var key in keys)
            Get(key)?.Invalidate();
    }

    public Task Prune()
    {
        lock (Lock) {
            if (_pruneTask == null || _pruneTask.IsCompleted)
                _pruneTask = Task.Run(PruneInternal);
            return _pruneTask;
        }
    }

    // Private methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnOperation(int random)
    {
        if (!_opCounter.Increment(random, out var opCounterValue))
            return;
        if (opCounterValue > _pruneCounterThreshold)
            TryPrune();
    }

    private void TryPrune()
    {
        lock (Lock) {
            // Double check locking
            if (_opCounter.ApproximateValue <= _pruneCounterThreshold)
                return;
            _opCounter.ApproximateValue = 0;
            Prune();
        }
    }

    private void PruneInternal()
    {
        var type = GetType();
        using var activity = type.GetActivitySource().StartActivity(type, nameof(Prune));

        // Debug.WriteLine(nameof(PruneInternal));
        var randomOffset = Randomize(Thread.CurrentThread.ManagedThreadId);
        foreach (var (key, handle) in _storage) {
            if (handle.Target == null && _storage.TryRemove(key, handle))
                _gcHandlePool.Release(handle, key.HashCode + randomOffset);
        }
        lock (Lock) {
            UpdatePruneCounterThreshold();
            _opCounter.ApproximateValue = 0;
        }
    }

    private void UpdatePruneCounterThreshold()
    {
        lock (Lock) {
            // Should be called inside Lock
            var capacity = (long) _storage.GetCapacity();
            var nextThreshold = (int) Math.Min(int.MaxValue >> 1, capacity);
            _pruneCounterThreshold = nextThreshold;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Randomize(int random)
        => random + CoarseClockHelper.RandomInt32;
}
