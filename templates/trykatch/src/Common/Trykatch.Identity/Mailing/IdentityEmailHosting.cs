using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trykatch.Application.Mailing;
using Trykatch.Modules;

namespace Trykatch.Identity;

internal static class IdentityEmailHosting
{
    public static IServiceCollection AddIdentityEmailDelivery(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment,
        bool isOpenApiGeneration)
    {
#if TRYKATCH_EMAIL
        string? identityConnection = configuration.GetConnectionString("trykatch-identity");
        if (!isOpenApiGeneration && !string.IsNullOrWhiteSpace(identityConnection))
        {
            services.AddSingleton<DbContextOptions<IdentityEmailDeliveryDbContext>>(_ =>
            {
                DbContextOptionsBuilder<IdentityEmailDeliveryDbContext> options = new();
                options.UseNpgsql(identityConnection);
                return options.Options;
            });
            services.AddSingleton<IdentityEmailDeliveryStore>();
            services.AddSingleton(new IdentityEmailLinkPolicy(configuration, isDevelopment));
            services.AddScoped<IdentitySecurityEmailDelivery>();
            services.TryAddScoped<IIdentitySecurityEmailSubmission>(provider =>
                provider.GetRequiredService<IdentitySecurityEmailDelivery>());
            services.AddScoped<IEmailDeliverySource>(provider =>
                provider.GetRequiredService<IdentitySecurityEmailDelivery>());
            return services;
        }
#endif

        services.TryAddSingleton<IIdentitySecurityEmailSubmission, UnavailableIdentitySecurityEmailSubmission>();
        return services;
    }

    private sealed class UnavailableIdentitySecurityEmailSubmission : IIdentitySecurityEmailSubmission
    {
        public bool IsConfigured => false;

        public Task<ModuleWorkReceipt> EnqueuePasswordResetAsync(
            PasswordResetEmailIntent intent,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(intent);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ModuleWorkReceipt(
                intent.DeliveryId,
                ModuleWorkState.Unavailable,
                FailureCode: "email_unavailable"));
        }
    }
}
