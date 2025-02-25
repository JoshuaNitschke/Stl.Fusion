namespace Stl.Fusion;

public interface IComputedState : IState, IDisposable, IHasWhenDisposed
{
    public new interface IOptions : IState.IOptions
    {
        IUpdateDelayer? UpdateDelayer { get; init; }
    }

    IUpdateDelayer UpdateDelayer { get; set; }
    Task UpdateCycleTask { get; }
    CancellationToken DisposeToken { get; }
}

public interface IComputedState<T> : IState<T>, IComputedState
{ }

public abstract class ComputedState<T> : State<T>, IComputedState<T>
{
    public new record Options : State<T>.Options, IComputedState.IOptions
    {
        public IUpdateDelayer? UpdateDelayer { get; init; }
    }

    private volatile IUpdateDelayer _updateDelayer;
    private volatile Task? _whenDisposed;
    private readonly CancellationTokenSource _disposeTokenSource;

    protected ILogger Log { get; }

    public IUpdateDelayer UpdateDelayer {
        get => _updateDelayer;
        set => _updateDelayer = value;
    }

    public CancellationToken DisposeToken { get; }
    public Task UpdateCycleTask { get; private set; } = null!;
    public Task? WhenDisposed => _whenDisposed;

    protected ComputedState(Options options, IServiceProvider services, bool initialize = true)
        : base(options, services, false)
    {
        Log = Services.GetService<ILoggerFactory>()?.CreateLogger(GetType()) ?? NullLogger.Instance;
        _disposeTokenSource = new CancellationTokenSource();
        DisposeToken = _disposeTokenSource.Token;
        _updateDelayer = options.UpdateDelayer ?? Services.GetRequiredService<IUpdateDelayer>();
        // ReSharper disable once VirtualMemberCallInConstructor
        if (initialize) Initialize(options);
    }

    protected override void Initialize(State<T>.Options options)
    {
        if (UpdateCycleTask != null!)
            return;
        base.Initialize(options);

        // We must suppress execution context flow here, because
        // the Update is ~ a worker-style task
        if (ExecutionContext.IsFlowSuppressed())
            UpdateCycleTask = Task.Run(UpdateCycle, CancellationToken.None);
        else {
            using var _ = ExecutionContext.SuppressFlow();
            UpdateCycleTask = Task.Run(UpdateCycle, CancellationToken.None);
        }
    }

    // ~ComputedState() => Dispose();

    public virtual void Dispose()
    {
        if (_whenDisposed != null)
            return;
        lock (Lock) {
            if (_whenDisposed != null)
                return;
            _whenDisposed = UpdateCycleTask ?? Task.CompletedTask;
            GC.SuppressFinalize(this);
            _disposeTokenSource.CancelAndDisposeSilently();
        }
    }

    protected virtual async Task UpdateCycle()
    {
        var cancellationToken = DisposeToken;
        while (!cancellationToken.IsCancellationRequested) {
            try {
                var snapshot = Snapshot;
                var computed = snapshot.Computed;
                if (!computed.IsInvalidated())
                    await computed.WhenInvalidated(cancellationToken).ConfigureAwait(false);
                if (snapshot.UpdateCount != 0)
                    await UpdateDelayer.Delay(snapshot, cancellationToken).ConfigureAwait(false);
                if (!snapshot.WhenUpdated().IsCompleted)
                    await computed.Update(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                // Will break from "while" loop later if it's due to cancellationToken cancellation
            }
            catch (Exception e) {
                Log.LogError(e, "Failure inside UpdateCycle()");
            }
        }
    }
}
