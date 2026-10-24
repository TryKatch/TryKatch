using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trykatch.Api;
using Trykatch.Api.Realtime;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.ExecutionAcceptance;

internal static class RealtimeHost
{
    private const int MaximumCommandBytes = 4_096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync()
    {
        TextWriter protocolOutput = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            string applicationRoot = RequiredDirectory("TRYKATCH_ACCEPTANCE_APPLICATION_ROOT");
            string instance = Required("TRYKATCH_ACCEPTANCE_INSTANCE");
            string certificatePath = RequiredPath("TRYKATCH_ACCEPTANCE_TLS_CERTIFICATE_PATH");
            string certificatePassword = Required("TRYKATCH_ACCEPTANCE_TLS_CERTIFICATE_PASSWORD");
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(global::Program).Assembly.GetName().Name,
                ContentRootPath = applicationRoot,
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            string apiSettingsPath = Path.Combine(applicationRoot, "src", "API", "Trykatch.Api", "appsettings.json");
            builder.Configuration.AddJsonFile(apiSettingsPath, optional: true, reloadOnChange: false);
            // Reapply the isolated process environment after adding the API's source-owned settings.
            builder.Configuration.AddEnvironmentVariables();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                endpoint => endpoint.UseHttps(certificatePath, certificatePassword)));

            bool openApiGeneration = builder.AddApplicationApi();
            MutationCrashProbe? mutationProbe = MutationCrashProbe.Register(builder.Services, protocolOutput);
            await using WebApplication app = builder.Build();
            await app.InitializeApplicationApiAsync(openApiGeneration);
            mutationProbe?.UseNetworkCheckpoint(app);
            app.UseApplicationApi();
            await app.StartAsync(CancellationToken.None);

            string address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
                ?? throw new InvalidOperationException("Kestrel did not publish one test endpoint.");
            RealtimeApiReady ready = new("realtime-ready", Environment.ProcessId, instance, address,
                await RuntimeRolesAsync(builder.Configuration));
            await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(ready, JsonOptions));
            await protocolOutput.FlushAsync();

            while (await ReadBoundedCommandLineAsync() is { } incoming)
            {
                if (incoming.Oversized)
                {
                    await WriteResponseAsync(protocolOutput, new("command-rejected", null));
                    continue;
                }

                RealtimeControlCommand? command;
                try { command = JsonSerializer.Deserialize<RealtimeControlCommand>(incoming.Text, JsonOptions); }
                catch (JsonException)
                {
                    await WriteResponseAsync(protocolOutput, new("command-rejected", null));
                    continue;
                }

                if (command is not { Action: "send-probe" }
                    || string.IsNullOrWhiteSpace(command.ConnectionId) || command.ConnectionId.Length > 128
                    || !Guid.TryParse(command.Nonce, out Guid nonce) || nonce == Guid.Empty)
                {
                    await WriteResponseAsync(protocolOutput, new("command-rejected", null));
                    continue;
                }

                string result = await SendProbeAsync(app.Services, command.ConnectionId, nonce);
                await WriteResponseAsync(protocolOutput, new(result, nonce.ToString("D")));
            }

            await app.StopAsync(CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"REALTIME_HOST_FAILED {exception.GetType().Name}");
            return 1;
        }
    }

    private static async Task<string> SendProbeAsync(IServiceProvider services, string connectionId, Guid nonce)
    {
        try
        {
            IHubContext<ModuleRealtimeHub> hub = services.GetRequiredService<IHubContext<ModuleRealtimeHub>>();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await hub.Clients.Client(connectionId).SendAsync("backplane-probe", nonce.ToString("D"), timeout.Token);
            return "send-completed";
        }
        catch (Exception)
        {
            // The test observes the nonce at a real remote client; this status is transport diagnostics only.
            return "send-failed";
        }
    }

    private static async Task WriteResponseAsync(TextWriter protocolOutput, RealtimeControlResponse response)
    {
        await protocolOutput.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        await protocolOutput.FlushAsync();
    }

    private static async Task<BoundedCommandLine?> ReadBoundedCommandLineAsync()
    {
        char[] character = new char[1];
        System.Text.StringBuilder line = new(MaximumCommandBytes);
        bool oversized = false;
        while (true)
        {
            int read = await Console.In.ReadAsync(character.AsMemory());
            if (read == 0) return line.Length == 0 && !oversized ? null : new(line.ToString(), oversized);
            if (character[0] == '\n') return new(line.ToString(), oversized);
            if (character[0] == '\r') continue;
            if (line.Length < MaximumCommandBytes) line.Append(character[0]);
            else oversized = true;
        }
    }

    private static async Task<RuntimeRoleNames> RuntimeRolesAsync(IConfiguration configuration)
    {
        return new(
            await RoleAsync(configuration, RuntimeDatabaseConnectionContract.Organization),
            await RoleAsync(configuration, RuntimeDatabaseConnectionContract.Platform),
            await RoleAsync(configuration, RuntimeDatabaseConnectionContract.Identity),
            await RoleAsync(configuration, RuntimeDatabaseConnectionContract.Outbox),
            await RoleAsync(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator));
    }

    private static async Task<string> RoleAsync(IConfiguration configuration, string connectionName)
    {
        string connectionString = configuration.GetConnectionString(connectionName)
            ?? throw new InvalidOperationException("A required runtime database profile is missing.");
        await using NpgsqlConnection connection = new(connectionString);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await connection.OpenAsync(timeout.Token);
        await using NpgsqlCommand command = new("SELECT current_user", connection);
        string? currentUser = (string?)await command.ExecuteScalarAsync(timeout.Token);
        return currentUser ?? throw new InvalidOperationException("A runtime database role could not be verified.");
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException("A required realtime acceptance setting is missing.");

    private static string RequiredPath(string name)
    {
        string value = Required(name);
        if (!Path.IsPathFullyQualified(value) || !File.Exists(value))
            throw new InvalidOperationException("A required realtime acceptance file is unavailable.");
        return value;
    }

    private static string RequiredDirectory(string name)
    {
        string value = Required(name);
        if (!Path.IsPathFullyQualified(value) || !Directory.Exists(value))
            throw new InvalidOperationException("A required realtime acceptance directory is unavailable.");
        return value;
    }
}

public sealed record RuntimeRoleNames(
    string Organization,
    string Platform,
    string Identity,
    string Outbox,
    string WorkCoordinator);

public sealed record RealtimeApiReady(
    string Kind,
    int ProcessId,
    string Instance,
    string Address,
    RuntimeRoleNames Roles);

public sealed record RealtimeControlCommand(string? Action, string? ConnectionId, string? Nonce);

public sealed record RealtimeControlResponse(string Outcome, string? Nonce);

public sealed record BoundedCommandLine(string Text, bool Oversized);
