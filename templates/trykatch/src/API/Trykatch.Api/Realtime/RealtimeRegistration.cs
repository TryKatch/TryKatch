using Microsoft.Extensions.Options;
using Trykatch.Infrastructure;

namespace Trykatch.Api.Realtime;

internal static class RealtimeRegistration
{
    internal static void AddRealtime(this IServiceCollection services, IConfiguration configuration)
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
    }
}
