using FlatpackApp.Modules.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlatpackApp.Modules.Federation;

public sealed class FederationModule : IFlatpackModule, IFlatpackPlatformEndpointContributor, IFlatpackModuleMigrationContributor
{
    private const string ReadPermission = "platform.authentication.read";
    private const string ManagePermission = "platform.authentication.manage";

    public string ModuleId => Descriptor.Id;
    public string RequiredPlatformPermission => ReadPermission;

    public FlatpackModuleDescriptor Descriptor { get; } = new(
        "federation",
        "Single sign-on",
        "1.0.0",
        "Generic OpenID Connect connection management and federation security policy.",
        [],
        [],
        FlatpackModuleCapabilities.Api | FlatpackModuleCapabilities.Web | FlatpackModuleCapabilities.Data,
        []);

    public IReadOnlyList<FlatpackModuleMigration> Migrations { get; } =
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
        string connectionString = configuration.GetConnectionString("flatpackdb")
            ?? throw new InvalidOperationException("Connection string 'flatpackdb' is required by Federation.");
        services.AddSingleton<IFlatpackPlatformEndpointContributor>(this);
        services.AddSingleton(new FederationDatabaseOptions(connectionString));
        services.AddScoped<FederationConnectionStore>();
        services.AddScoped<FederationConnectionService>();
        services.AddHttpClient("flatpack-federation-discovery", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Flatpack-Federation/1.0");
        });
        services.AddOptions<FederationSecurityOptions>()
            .Bind(configuration.GetSection("Federation"));
    }

    public void MapEndpoints(RouteGroupBuilder platformApi)
    {
        RouteGroupBuilder group = platformApi.MapGroup("/federation/connections");
        group.MapGet("/", async (FederationConnectionStore store, CancellationToken cancellationToken) =>
                Results.Ok(await store.ListAsync(cancellationToken)))
            .WithName("Federation_ListConnections")
            .WithTags("Federation");
        group.MapPost("/", CreateAsync)
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .WithName("Federation_CreateConnection")
            .WithTags("Federation");
        group.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .WithName("Federation_UpdateConnection")
            .WithTags("Federation");
        group.MapPost("/{id:guid}/test", TestAsync)
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .WithName("Federation_TestConnection")
            .WithTags("Federation");
        group.MapPost("/{id:guid}/enable", (Guid id, FederationConnectionService service, CancellationToken cancellationToken) =>
                ChangeStatusAsync(id, true, service, cancellationToken))
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .WithName("Federation_EnableConnection")
            .WithTags("Federation");
        group.MapPost("/{id:guid}/disable", (Guid id, FederationConnectionService service, CancellationToken cancellationToken) =>
                ChangeStatusAsync(id, false, service, cancellationToken))
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .WithName("Federation_DisableConnection")
            .WithTags("Federation");
        group.MapDelete("/{id:guid}", DeleteAsync)
            .RequireAuthorization($"platform-permission:{ManagePermission}")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .WithName("Federation_DeleteConnection")
            .WithTags("Federation");
    }

    private static async Task<IResult> CreateAsync(
        SaveFederationConnectionRequest request,
        FederationConnectionService service,
        CancellationToken cancellationToken)
    {
        FederationOperationResult<FederationConnection> result = await service.CreateAsync(request, cancellationToken);
        return ToResult(result, value => Results.Created($"/api/v1/platform/federation/connections/{value.Id}", value));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        SaveFederationConnectionRequest request,
        FederationConnectionService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.UpdateAsync(id, request, cancellationToken), Results.Ok);

    private static async Task<IResult> TestAsync(
        Guid id,
        FederationConnectionService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.TestAsync(id, cancellationToken), Results.Ok);

    private static async Task<IResult> ChangeStatusAsync(
        Guid id,
        bool enabled,
        FederationConnectionService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(id, enabled, cancellationToken), Results.Ok);

    private static async Task<IResult> DeleteAsync(
        Guid id,
        FederationConnectionService service,
        CancellationToken cancellationToken) =>
        ToResult(await service.DeleteAsync(id, cancellationToken), _ => Results.NoContent());

    private static IResult ToResult<T>(FederationOperationResult<T> result, Func<T, IResult> success) =>
        result.Value is not null
            ? success(result.Value)
            : Results.Problem(
                statusCode: result.Code switch
                {
                    "not_found" => StatusCodes.Status404NotFound,
                    "conflict" => StatusCodes.Status409Conflict,
                    "provider_unavailable" => StatusCodes.Status422UnprocessableEntity,
                    _ => StatusCodes.Status400BadRequest
                },
                title: result.Code,
                detail: result.Error);
}

public sealed class FederationSecurityOptions
{
    public bool AllowInsecureLoopbackIssuer { get; init; }
}

internal sealed record FederationDatabaseOptions(string ConnectionString);
internal sealed record FederationOperationResult<T>(T? Value, string? Code = null, string? Error = null)
{
    public static FederationOperationResult<T> Success(T value) => new(value);
    public static FederationOperationResult<T> Failure(string code, string error) => new(default, code, error);
}
