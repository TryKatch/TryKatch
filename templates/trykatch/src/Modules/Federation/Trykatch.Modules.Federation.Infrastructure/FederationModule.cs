using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Federation.Application;
using Trykatch.Modules.Federation.Presentation;

namespace Trykatch.Modules.Federation.Infrastructure;

public sealed class FederationModule : IModule, IModuleMigrationContributor
{
    public string ModuleId => Descriptor.Id;

    public ModuleDescriptor Descriptor { get; } = new(
        "federation", "Single sign-on", "1.0.0",
        "Generic OpenID Connect connection management and federation security policy.",
        [], [],
        ModuleCapabilities.Api | ModuleCapabilities.Web | ModuleCapabilities.Data,
        [])
    {
        DefaultDataOwnership = ModuleDataOwnership.Platform,
        DataResources =
        [
            new("federation-connections", "identity", "federation_connections",
                ModuleDataOwnership.Platform, AccessRule: ModuleDataAccessRule.IdentityOnly)
        ]
    };

    public IReadOnlyList<ModuleMigration> Migrations { get; } =
    [
        new("202609081900_initial", """
            CREATE TABLE identity.federation_connections
            (
                id uuid PRIMARY KEY,
                name character varying(120) NOT NULL,
                issuer character varying(500) NOT NULL,
                client_id character varying(240) NOT NULL,
                protected_client_secret text NULL,
                enabled boolean NOT NULL DEFAULT false,
                tested_configuration_hash character(64) NULL,
                last_tested_at timestamp with time zone NULL,
                last_test_result character varying(500) NULL,
                created_at timestamp with time zone NOT NULL,
                updated_at timestamp with time zone NOT NULL,
                deleted_at timestamp with time zone NULL,
                CONSTRAINT uq_federation_connections_issuer_client UNIQUE (issuer, client_id)
            );
            CREATE INDEX ix_federation_connections_active
                ON identity.federation_connections (enabled, name)
                WHERE deleted_at IS NULL;
            """)
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("trykatch-identity")
            ?? configuration.GetConnectionString("trykatchdb")
            ?? throw new InvalidOperationException("Connection string 'trykatch-identity' is required by Federation.");
        services.AddSingleton<IPlatformEndpointContributor, FederationEndpoints>();
        services.AddSingleton(new FederationDatabaseOptions(connectionString));
        services.AddScoped<IFederationConnectionStore, FederationConnectionStore>();
        services.AddScoped<IFederationProviderProbe, FederationProviderProbe>();
        services.AddScoped<FederationConnectionService>();
        services.AddHttpClient("trykatch-federation-discovery", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Trykatch-Federation/1.0");
        }).ConfigurePrimaryHttpMessageHandler(provider => FederationNetworkGuard.CreateHandler(
            provider.GetRequiredService<IOptions<FederationSecurityOptions>>().Value.AllowInsecureLoopbackIssuer));
        services.AddOptions<FederationSecurityOptions>().Bind(configuration.GetSection("Federation"));
    }
}

internal sealed record FederationDatabaseOptions(string ConnectionString);
