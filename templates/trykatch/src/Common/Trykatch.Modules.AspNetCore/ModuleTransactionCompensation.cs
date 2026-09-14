using Trykatch.Modules;

namespace Trykatch.Modules.AspNetCore;

/// <summary>
/// Request-scoped transaction participant for compensating external side effects.
/// The application host completes or rolls back the participant with its database transaction.
/// </summary>
public sealed class ModuleTransactionCompensation : IModuleTransactionCompensation
{
    private readonly Lock gate = new();
    private readonly List<Func<CancellationToken, Task>> callbacks = [];

    public void EnlistRollback(Func<CancellationToken, Task> compensation)
    {
        ArgumentNullException.ThrowIfNull(compensation);
        lock (gate)
        {
            callbacks.Add(compensation);
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            callbacks.Clear();
        }
    }

    /// <summary>
    /// Runs destructive compensations only after the database rollback succeeds.
    /// A failed rollback can represent an indeterminate commit, so callbacks remain
    /// pending and the external resource is retained for reconciliation.
    /// </summary>
    public async Task CompensateAfterConfirmedRollbackAsync(
        Func<CancellationToken, Task> rollback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        await rollback(cancellationToken);
        await RollbackAsync(cancellationToken);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task>[] pending;
        lock (gate)
        {
            pending = callbacks.AsEnumerable().Reverse().ToArray();
            callbacks.Clear();
        }

        List<Exception>? failures = null;
        foreach (Func<CancellationToken, Task> compensation in pending)
        {
            try
            {
                await compensation(cancellationToken);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more module transaction compensations failed.", failures);
    }
}
