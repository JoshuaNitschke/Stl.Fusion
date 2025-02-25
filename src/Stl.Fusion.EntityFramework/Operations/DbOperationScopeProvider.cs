using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Stl.Fusion.Operations.Reprocessing;

namespace Stl.Fusion.EntityFramework.Operations;

public class DbOperationScopeProvider<TDbContext> : DbServiceBase<TDbContext>, ICommandHandler<ICommand>
    where TDbContext : DbContext
{
    // ReSharper disable once StaticMemberInGenericType
    protected static MemberInfo ExecutionStrategyShouldRetryOnMethod { get; } = typeof(ExecutionStrategy)
        .GetMethod("ShouldRetryOn", BindingFlags.Instance | BindingFlags.NonPublic)!;

    protected IOperationCompletionNotifier OperationCompletionNotifier { get; }

    public DbOperationScopeProvider(IServiceProvider services) : base(services) 
        => OperationCompletionNotifier = services.GetRequiredService<IOperationCompletionNotifier>();

    [CommandHandler(Priority = 1000, IsFilter = true)]
    public async Task OnCommand(ICommand command, CommandContext context, CancellationToken cancellationToken)
    {
        var operationRequired =
            context.OuterContext == null // Should be a top-level command
            && !(command is IMetaCommand) // No operations for meta commands
            && !Computed.IsInvalidating();
        if (!operationRequired) {
            await context.InvokeRemainingHandlers(cancellationToken).ConfigureAwait(false);
            return;
        }

        var tScope = typeof(DbOperationScope<TDbContext>);
        if (context.Items[tScope] != null) // Safety check
            throw Stl.Internal.Errors.InternalError($"'{tScope}' scope is already provided. Duplicate handler?");

        var scope = Services.GetRequiredService<DbOperationScope<TDbContext>>();
        await using var _ = scope.ConfigureAwait(false);
        var operation = scope.Operation;
        operation.Command = command;
        context.Items.Set(scope);

        try {
            await context.InvokeRemainingHandlers(cancellationToken).ConfigureAwait(false);
            if (!scope.IsClosed)
                await scope.Commit(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException) {
            try {
                var operationReprocessor = context.Items.Get<IOperationReprocessor>();
                if (operationReprocessor == null)
                    throw;

                var allErrors = error.Flatten();
                var transientError = allErrors.FirstOrDefault(scope.IsTransientFailure);
                if (transientError == null)
                    throw;

                // It's a transient failure - let's tag it so that IOperationReprocessor retries on it 
                operationReprocessor.AddTransientFailure(transientError);

                // But if retry still won't happen (too many retries?) - let's log error here
                if (!operationReprocessor.WillRetry(allErrors))
                    Log.LogError(error, "Operation failed: {Command}", command);
                else 
                    Log.LogInformation("Transient failure on {Command}: {TransientError}", 
                        command, transientError.ToExceptionInfo());

                throw;
            }
            finally {
                // 7. Ensure everything is rolled back
                try {
                    await scope.Rollback().ConfigureAwait(false);
                }
                catch {
                    // Intended
                }
            }
        }
    }
}
