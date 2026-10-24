using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Modules;

namespace Trykatch.ExecutionAcceptance;

/// <summary>Private process-test barriers; this assembly is never part of the deployed API.</summary>
internal sealed class MutationCrashProbe(string phase, TextWriter protocolOutput)
{
    private int reached;

    public static MutationCrashProbe? Register(IServiceCollection services, TextWriter protocolOutput)
    {
        string? phase = Environment.GetEnvironmentVariable("TRYKATCH_ACCEPTANCE_MUTATION_CRASH_PHASE");
        if (phase is null) return null;
        if (phase is not ("before-commit" or "after-commit"))
            throw new InvalidOperationException("Unsupported mutation acceptance checkpoint.");
        MutationCrashProbe probe = new(phase, protocolOutput);
        if (phase == "before-commit")
        {
            ServiceDescriptor original = services.Single(descriptor => descriptor.ServiceType == typeof(IModuleMutationIdempotencyStore));
            Type implementation = original.ImplementationType
                ?? throw new InvalidOperationException("The mutation acceptance store must have an explicit implementation.");
            services.Remove(original);
            services.AddScoped(implementation);
            services.AddScoped<IModuleMutationIdempotencyStore>(provider =>
                new BeforeCommitStore((IModuleMutationIdempotencyStore)provider.GetRequiredService(implementation), probe));
        }
        return probe;
    }

    public void UseNetworkCheckpoint(WebApplication app)
    {
        if (phase != "after-commit") return;
        app.Use(async (context, next) =>
        {
            // The real server's OnStarting runs when AtomicMutationResponse copies its
            // staged bytes to the network. The parent independently verifies the commit.
            context.Response.OnStarting(async () =>
            {
                if (context.Response.StatusCode == StatusCodes.Status201Created
                    && context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "Projects_Create")
                    await PauseAsync();
            });
            await next(context);
        });
    }

    private async Task PauseAsync()
    {
        if (Interlocked.Exchange(ref reached, 1) != 0)
            throw new InvalidOperationException("A mutation acceptance checkpoint can be reached once.");
        await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(new { kind = "mutation-checkpoint", phase }));
        await protocolOutput.FlushAsync();
        // Only the parent process can release this barrier, by killing this process.
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private sealed class BeforeCommitStore(IModuleMutationIdempotencyStore inner, MutationCrashProbe probe) : IModuleMutationIdempotencyStore
    {
        private bool selected;

        public Task<MutationReservationResult> ReserveAsync(MutationRequest request,
            Func<MutationAuthority, CancellationToken, Task<bool>> authorizeRequest, CancellationToken cancellationToken)
        {
            selected = request.ModuleId == "projects" && request.OperationId == "Projects_Create";
            return inner.ReserveAsync(request, authorizeRequest, cancellationToken);
        }

        public async Task CompleteAsync(Guid reservationId, MutationReceipt receipt, CancellationToken cancellationToken)
        {
            await inner.CompleteAsync(reservationId, receipt, cancellationToken);
            if (selected) await probe.PauseAsync();
        }
    }
}
