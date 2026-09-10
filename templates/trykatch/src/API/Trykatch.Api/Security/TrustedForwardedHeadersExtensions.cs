using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Trykatch.Api.Security;

public static class TrustedForwardedHeadersExtensions
{
    public static IServiceCollection AddTrustedForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string[] configuredProxies = configuration
            .GetSection("ReverseProxy:KnownProxies")
            .Get<string[]>() ?? [];

        IPAddress[] knownProxies = configuredProxies
            .Select(ParseProxyAddress)
            .Distinct()
            .ToArray();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            if (knownProxies.Length == 0)
            {
                options.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;

            foreach (IPAddress knownProxy in knownProxies)
            {
                options.KnownProxies.Add(knownProxy);
            }
        });

        return services;
    }

    private static IPAddress ParseProxyAddress(string configuredProxy)
    {
        if (IPAddress.TryParse(configuredProxy, out IPAddress? address))
        {
            return address;
        }

        throw new InvalidOperationException(
            $"ReverseProxy:KnownProxies contains invalid IP address '{configuredProxy}'. " +
            "Configure the exact address of each trusted reverse proxy.");
    }
}
