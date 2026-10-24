using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Connections;
using Trykatch.Identity;
using Trykatch.Infrastructure;

namespace Trykatch.Api.Realtime;

internal static class RealtimeRegistration
{
    internal static void AddRealtime(this IServiceCollection services, IConfiguration configuration, bool isOpenApiGeneration)
    {
        RealtimeOptions settings;
        try
        {
            settings = configuration.GetSection(RealtimeOptions.SectionName)
                .Get<RealtimeOptions>(binding => binding.ErrorOnUnknownConfiguration = true) ?? new();
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("Realtime configuration is invalid.");
        }
        ValidateOptionsResult validation = new RealtimeOptionsValidator().Validate(Options.DefaultName, settings);
        if (validation.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(RealtimeOptions), validation.Failures);
        services.AddSingleton<IOptions<RealtimeOptions>>(Options.Create(settings));
        services.AddRealtimeHistoryCursors();
        services.AddScoped<RealtimeHttpReader>();
        services.AddScoped<RealtimeAuthority>();
        services.AddSingleton<RealtimeConnectionRegistry>();
        services.AddSingleton<RealtimeConnectionOperations>();
        if (settings.Mode != RealtimeMode.Disabled && !isOpenApiGeneration) services.AddHostedService<RealtimeRelay>();
        services.AddSingleton<RealtimeOriginPolicy>();
        services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = RealtimeOptions.MaximumIncomingBytes;
            options.MaximumParallelInvocationsPerClient = 1;
            options.StreamBufferCapacity = 1;
            options.EnableDetailedErrors = false;
            options.HandshakeTimeout = TimeSpan.FromSeconds(5);
            options.ClientTimeoutInterval = TimeSpan.FromSeconds(15);
            options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        });
    }

    internal static void MapRealtimeHub(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<RealtimeOptions>>().Value.Mode == RealtimeMode.Disabled) return;
        app.MapHub<ModuleRealtimeHub>("/api/v1/realtime/hub", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.CloseOnAuthenticationExpiration = true;
            options.AllowStatefulReconnects = false;
            options.ApplicationMaxBufferSize = 16_384;
            options.TransportMaxBufferSize = 16_384;
        }).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AuthenticationSchemes.ApplicationCookie })
            .WithMetadata(new RealtimeTransportMetadata());
    }
}
