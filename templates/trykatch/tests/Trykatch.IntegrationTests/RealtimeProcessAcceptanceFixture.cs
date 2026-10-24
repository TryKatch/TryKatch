using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application;
using Trykatch.Application.Execution;
using Trykatch.Application.Outbox;
using Trykatch.ExecutionAcceptance;
using Trykatch.Identity;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Modules;
using Trykatch.Api.Modules;

namespace Trykatch.IntegrationTests;

internal sealed class RealtimeProcessAcceptanceFixture : IAsyncDisposable
{
    private const string ValkeyImage = "valkey/valkey:9.1.2-alpine3.24@sha256:48332870af354a799964c0012ae1194a0bf2bf894eb508f945810596dc2d8d11";
    private const string ValkeyPassword = "Local-only!Realtime-Valkey-Password-42";
    private const string PlatformAdminEmail = "realtime-admin@trykatch.test";
    private const string PlatformAdminPassword = "Local-only!Realtime-Admin-Password-42";
    private readonly PostgreSqlContainer postgres;
    private readonly IContainer valkey;
    private readonly IdentityCertificateFixture certificates;
    private readonly string applicationRoot;
    private readonly string valkeyConnection;
    private readonly int fixedValkeyPort;
    private readonly Dictionary<string, string?> identityCertificateSettings;
    private readonly string[] apiInstances = ["replica-a", "replica-b", "prefix-probe"];
    private readonly List<RealtimeAcceptanceApiProcess> processes = [];
    private readonly List<HubProbeClient> clients = [];
    private readonly string tlsThumbprint;
    private readonly string tlsCertificatePath;
    private bool disposed;

    private RealtimeProcessAcceptanceFixture(PostgreSqlContainer postgres, IContainer valkey,
        IdentityCertificateFixture certificates, string applicationRoot, string valkeyConnection,
        int fixedValkeyPort, string tlsThumbprint, string tlsCertificatePath,
        Dictionary<string, string?> identityCertificateSettings)
    {
        this.postgres = postgres;
        this.valkey = valkey;
        this.certificates = certificates;
        this.applicationRoot = applicationRoot;
        this.valkeyConnection = valkeyConnection;
        this.fixedValkeyPort = fixedValkeyPort;
        this.tlsThumbprint = tlsThumbprint;
        this.tlsCertificatePath = tlsCertificatePath;
        this.identityCertificateSettings = identityCertificateSettings;
    }

    public static async Task<RealtimeProcessAcceptanceFixture> StartAsync()
    {
        DirectoryInfo appRoot = FindApplicationRoot();
        IdentityCertificateFixture certificates = new();
        PostgreSqlContainer postgres = new PostgreSqlBuilder(
            "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build();
        int fixedPort = ReserveLoopbackPort();
        IContainer valkey = new ContainerBuilder(ValkeyImage)
            .WithPortBinding(fixedPort, 6379)
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.PortBindings!["6379/tcp"]!.Single().HostIP = "127.0.0.1")
            .WithCommand("valkey-server", "--requirepass", ValkeyPassword, "--protected-mode", "yes")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        string valkeyConnection = $"127.0.0.1:{fixedPort},password={ValkeyPassword},abortConnect=false";

        try
        {
            await postgres.StartAsync();
            await valkey.StartAsync();
            string owner = postgres.GetConnectionString();
            await InitializeDatabaseAsync(owner);
            string tlsPath = certificates.Create("realtime-loopback-tls");
            using X509Certificate2 tlsCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                tlsPath, IdentityCertificateFixture.Password, X509KeyStorageFlags.DefaultKeySet);
            string passwordFile = Path.Combine(certificates.DirectoryPath, "acceptance-certificate-password");
            await File.WriteAllTextAsync(passwordFile, IdentityCertificateFixture.Password + "\n");
            Dictionary<string, string?> identityCertificateSettings = CreateIdentityCertificateSettings(certificates, passwordFile);
            return new(postgres, valkey, certificates, appRoot.FullName, valkeyConnection,
                fixedPort, tlsCertificate.Thumbprint, tlsPath, identityCertificateSettings);
        }
        catch
        {
            await valkey.DisposeAsync();
            await postgres.DisposeAsync();
            certificates.Dispose();
            throw;
        }
    }

    public async Task<RealtimeAcceptanceApiProcess> StartApiReplicaAsync(string instance,
        string applicationNamespace = "s2-acceptance", string? applicationPublicUrl = null)
    {
        if (!apiInstances.Contains(instance, StringComparer.Ordinal))
            throw new InvalidOperationException("Realtime acceptance instance name is not allowlisted.");
        string ownerConnection = postgres.GetConnectionString();
        (string organization, string platform, string identity, string outbox) =
            await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(ownerConnection);
        string coordinator = new NpgsqlConnectionStringBuilder(ownerConnection)
        {
            Username = PostgresRuntimeRoleFixture.WorkCoordinatorRole,
            Password = PostgresRuntimeRoleFixture.WorkCoordinatorPassword
        }.ConnectionString;
        Dictionary<string, string?> settings = new(identityCertificateSettings, StringComparer.Ordinal);
        AddConnection(settings, RuntimeDatabaseConnectionContract.Organization, organization);
        AddConnection(settings, RuntimeDatabaseConnectionContract.Platform, platform);
        AddConnection(settings, RuntimeDatabaseConnectionContract.Identity, identity);
        AddConnection(settings, RuntimeDatabaseConnectionContract.Outbox, outbox);
        AddConnection(settings, RuntimeDatabaseConnectionContract.WorkCoordinator, coordinator);
        settings["TRYKATCH_ACCEPTANCE_APPLICATION_ROOT"] = applicationRoot;
        settings["TRYKATCH_ACCEPTANCE_INSTANCE"] = instance;
        settings["TRYKATCH_ACCEPTANCE_TLS_CERTIFICATE_PATH"] = tlsCertificatePath;
        settings["TRYKATCH_ACCEPTANCE_TLS_CERTIFICATE_PASSWORD"] = IdentityCertificateFixture.Password;
        settings["Bootstrap__PlatformAdminEmail"] = PlatformAdminEmail;
        settings["Bootstrap__PlatformAdminPassword"] = PlatformAdminPassword;
        settings["Application__PublicUrl"] = applicationPublicUrl;
        ApplicationPublicUrl = applicationPublicUrl;
        settings["Email__Host"] = "127.0.0.1";
        settings["Email__Port"] = "1";
        settings["Email__From"] = "realtime-acceptance@example.test";
        settings["Email__Security"] = "StartTls";
        settings["Realtime__Mode"] = "Distributed";
        settings["Realtime__ApplicationNamespace"] = applicationNamespace;
        settings["ConnectionStrings__realtime"] = valkeyConnection;
        RealtimeAcceptanceApiProcess process = await RealtimeAcceptanceApiProcess.StartAsync(instance, settings, tlsThumbprint);
        process.Roles.ShouldBe(new RuntimeRoleNames(
            PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OutboxRole,
            PostgresRuntimeRoleFixture.WorkCoordinatorRole));
        processes.Add(process);
        return process;
    }

    public CookieApiClient CreateClient(RealtimeAcceptanceApiProcess process) =>
        new(process.Address, tlsThumbprint);

    public HubProbeClient CreateHubClient(RealtimeAcceptanceApiProcess process, CookieContainer cookies)
    {
        HubProbeClient client = new(process.Address, cookies, tlsThumbprint);
        clients.Add(client);
        return client;
    }

    public async Task<CookieApiClient> CreateOrganizationOwnerAsync(RealtimeAcceptanceApiProcess process)
    {
        using CookieApiClient administrator = CreateClient(process);
        await PostJsonAsync(administrator, "/api/v1/auth/login", new
        {
            email = PlatformAdminEmail,
            password = PlatformAdminPassword,
            rememberMe = false
        });
        JsonElement created = await PostJsonAsync(administrator, "/api/v1/tenants", new
        {
            name = "Realtime acceptance workspace",
            slug = $"s2-{Guid.NewGuid():N}"[..30],
            administratorEmail = "realtime-owner@trykatch.test"
        });
        string token = created.GetProperty("invitationToken").GetString()
            ?? throw new InvalidOperationException("Organization owner invitation was not issued.");
        CookieApiClient owner = CreateClient(process);
        try
        {
            await PostJsonAsync(owner, "/api/v1/invitations/activate", new
            {
                token,
                firstName = "Realtime",
                lastName = "Owner",
                password = "Local-only!Realtime-Owner-Password-42"
            });
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public static async Task AssertCookieWorksOnOtherHostAsync(CookieApiClient cookieClient, RealtimeAcceptanceApiProcess other)
    {
        using HttpResponseMessage session = await cookieClient.SendToAsync(new Uri(other.Address), HttpMethod.Get, "/api/v1/auth/session");
        session.StatusCode.ShouldBe(HttpStatusCode.OK, await session.Content.ReadAsStringAsync());
        using HttpResponseMessage workspace = await cookieClient.SendToAsync(new Uri(other.Address), HttpMethod.Get, "/api/v1/workspace/current");
        workspace.StatusCode.ShouldBe(HttpStatusCode.OK, await workspace.Content.ReadAsStringAsync());
    }

    public async Task RestartValkeyAtSameEndpointAsync()
    {
        await valkey.StopAsync();
        await valkey.StartAsync();
        ((int)valkey.GetMappedPublicPort(6379)).ShouldBe(fixedValkeyPort);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, fixedValkeyPort, timeout.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    public static async Task SendUntilReceivedAsync(RealtimeAcceptanceApiProcess sender, HubProbeClient receiver,
        Guid nonce, TimeSpan timeout)
    {
        receiver.Expect(nonce);
        using CancellationTokenSource deadline = new(timeout);
        while (!receiver.HasReceived(nonce))
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { _ = await sender.SendProbeAsync(receiver.ConnectionId, nonce, TimeSpan.FromSeconds(4), deadline.Token); }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            if (!receiver.HasReceived(nonce)) await Task.Delay(200, deadline.Token);
        }
        receiver.AssertReceived(nonce);
    }

    public static async Task<string> SendAndWaitAsync(RealtimeAcceptanceApiProcess sender, HubProbeClient receiver,
        Guid nonce, TimeSpan timeout)
    {
        receiver.Expect(nonce);
        string outcome = await sender.SendProbeAsync(receiver.ConnectionId, nonce, TimeSpan.FromSeconds(4));
        await receiver.WaitForReceiptAsync(nonce, timeout);
        return outcome;
    }

    public string ValkeyEndpoint => $"127.0.0.1:{fixedValkeyPort}";
    internal string ApplicationRoot => applicationRoot;
    internal string CertificateRoot => certificates.DirectoryPath;
    internal string TlsCertificatePath => tlsCertificatePath;
    internal string TlsCertificateThumbprint => tlsThumbprint;
    internal string? ApplicationPublicUrl { get; private set; }

    public Task StopValkeyAsync() => valkey.StopAsync();

    private static Dictionary<string, string?> CreateIdentityCertificateSettings(
        IdentityCertificateFixture certificates, string passwordFile)
    {
        string signing = certificates.Create("openid-signing");
        string encryption = certificates.Create("openid-encryption");
        string protection = certificates.Create("data-protection");
        return new Dictionary<string, string?>
        {
            ["DataProtection:Certificate:Path"] = protection,
            ["DataProtection:Certificate:PasswordFile"] = passwordFile,
            ["OpenIddict:SigningCertificate:Path"] = signing,
            ["OpenIddict:SigningCertificate:PasswordFile"] = passwordFile,
            ["OpenIddict:EncryptionCertificate:Path"] = encryption,
            ["OpenIddict:EncryptionCertificate:PasswordFile"] = passwordFile
        };
    }

    private static async Task<JsonElement> PostJsonAsync(CookieApiClient client, string path, object body)
    {
        using HttpResponseMessage csrfResponse = await client.Client.GetAsync("/api/v1/auth/antiforgery");
        csrfResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await csrfResponse.Content.ReadAsStringAsync());
        JsonElement csrf = await csrfResponse.Content.ReadFromJsonAsync<JsonElement>();
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using HttpResponseMessage response = await client.Client.SendAsync(request);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        return response.StatusCode == HttpStatusCode.NoContent
            ? default
            : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task InitializeDatabaseAsync(string owner)
    {
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(owner);
        IConfiguration empty = new ConfigurationBuilder().Build();
        IModule[] modules = EnabledModules.All.ToArray();
        ModuleCatalog catalog = new(modules);

        await using (IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(owner).Options))
            await identity.Database.MigrateAsync();
        await using (PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(owner).Options))
            await platform.Database.MigrateAsync();

        ServiceCollection moduleServices = new();
        foreach (IModule module in catalog.Modules) module.Register(moduleServices, empty);
        using ServiceProvider moduleProvider = moduleServices.BuildServiceProvider();
        IApplicationModelContributor[] contributors = moduleProvider.GetServices<IApplicationModelContributor>().ToArray();
        await using (ApplicationDbContext application = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(owner).Options,
            contributors,
            moduleCatalog: catalog))
            await application.Database.MigrateAsync();

        foreach (PendingModuleMigration migration in ModuleMigrationPlan.Build(catalog.Modules, []))
        {
            await using NpgsqlConnection connection = new(owner);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(migration.Sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        await InstalledSchemaCatalog.SynchronizeAsync(owner, catalog.Descriptors.SelectMany(module =>
            module.DataResources.Select(resource => new InstalledDataResource(module.Id, resource))));
        await RuntimeRoleProvisioner.ProvisionAsync(owner, new RuntimeDatabaseRoles(
            PostgresRuntimeRoleFixture.OrganizationRole,
            PostgresRuntimeRoleFixture.PlatformRole,
            PostgresRuntimeRoleFixture.IdentityRole,
            PostgresRuntimeRoleFixture.OutboxRole,
            PostgresRuntimeRoleFixture.WorkCoordinatorRole));
    }

    private static void AddConnection(Dictionary<string, string?> settings, string name, string connection) =>
        settings[$"ConnectionStrings__{name}"] = connection;

    private static int ReserveLoopbackPort()
    {
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    private static DirectoryInfo FindApplicationRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Trykatch.slnx"))) return current;
        throw new DirectoryNotFoundException("Trykatch application root was not found from the integration-test output directory.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        foreach (HubProbeClient client in clients.AsEnumerable().Reverse()) await client.DisposeAsync();
        foreach (RealtimeAcceptanceApiProcess process in processes.AsEnumerable().Reverse()) await process.DisposeAsync();
        await valkey.DisposeAsync();
        await postgres.DisposeAsync();
        certificates.Dispose();
    }
}

internal sealed class CookieApiClient : IDisposable
{
    private readonly string certificateThumbprint;
    private readonly CookieContainer cookies;
    private readonly HttpClientHandler handler;

    public CookieApiClient(string address, string certificateThumbprint)
    {
        Address = new Uri(address);
        this.certificateThumbprint = certificateThumbprint;
        cookies = new CookieContainer();
        handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                string.Equals(certificate?.GetCertHashString(), certificateThumbprint, StringComparison.OrdinalIgnoreCase)
        };
        Client = new HttpClient(handler) { BaseAddress = Address };
    }

    public Uri Address { get; }
    public HttpClient Client { get; }
    public CookieContainer Cookies => cookies;

    public Task<HttpResponseMessage> SendToAsync(Uri address, HttpMethod method, string path) =>
        Client.SendAsync(new HttpRequestMessage(method, new Uri(address, path)));

    public void Dispose() => Client.Dispose();
}

internal sealed class HubProbeClient : IAsyncDisposable
{
    private readonly HubConnection connection;
    private readonly CookieContainer cookies;
    private readonly string certificateThumbprint;
    private readonly Uri baseAddress;
    private readonly Dictionary<string, string> headers;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> expected = new();
    private readonly ConcurrentDictionary<Guid, byte> received = new();

    public HubProbeClient(string address, CookieContainer cookies, string certificateThumbprint)
    {
        baseAddress = new(address);
        this.cookies = cookies;
        this.certificateThumbprint = certificateThumbprint;
        string origin = baseAddress.GetLeftPart(UriPartial.Authority);
        headers = new Dictionary<string, string> { ["Origin"] = origin };
        connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, "/api/v1/realtime/hub"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.Cookies = cookies;
                options.Headers = headers;
                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler clientHandler)
                        clientHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                            string.Equals(certificate?.GetCertHashString(), certificateThumbprint, StringComparison.OrdinalIgnoreCase);
                    return handler;
                };
                options.WebSocketConfiguration = socket =>
                {
                    socket.SetRequestHeader("Origin", origin);
                    socket.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        string.Equals(certificate?.GetCertHashString(), certificateThumbprint, StringComparison.OrdinalIgnoreCase);
                };
            })
            .Build();
        connection.On<string>("backplane-probe", value =>
        {
            if (Guid.TryParse(value, out Guid nonce))
            {
                received.TryAdd(nonce, 0);
                if (expected.TryGetValue(nonce, out TaskCompletionSource? receipt)) receipt.TrySetResult();
            }
        });
    }

    public string ConnectionId => connection.ConnectionId
        ?? throw new InvalidOperationException("Realtime test client has no live connection ID.");

    public async Task StartAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using HttpClientHandler handler = new()
        {
            CookieContainer = cookies,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                string.Equals(certificate?.GetCertHashString(), certificateThumbprint, StringComparison.OrdinalIgnoreCase)
        };
        using HttpClient browser = new(handler) { BaseAddress = baseAddress };
        using HttpResponseMessage response = await browser.GetAsync("/api/v1/auth/antiforgery", timeout.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement antiforgery = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        headers["X-CSRF-TOKEN"] = antiforgery.GetProperty("token").GetString()!;
        await connection.StartAsync(timeout.Token);
        await connection.InvokeAsync("ReplaceSubscriptions", Array.Empty<ModuleRealtimeAudience>(), timeout.Token);
        ConnectionId.ShouldNotBeNullOrWhiteSpace();
    }

    public void Expect(Guid nonce) => expected.TryAdd(nonce,
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public bool HasReceived(Guid nonce) => received.ContainsKey(nonce);

    public async Task WaitForReceiptAsync(Guid nonce, TimeSpan timeout)
    {
        TaskCompletionSource receipt = expected.GetOrAdd(nonce,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        using CancellationTokenSource deadline = new(timeout);
        await receipt.Task.WaitAsync(deadline.Token);
        received.ContainsKey(nonce).ShouldBeTrue();
    }

    public void AssertReceived(Guid nonce) => received.ContainsKey(nonce).ShouldBeTrue();

    public async Task<bool> WaitForNoReceiptAsync(Guid nonce, TimeSpan duration)
    {
        using CancellationTokenSource timeout = new(duration);
        try
        {
            TaskCompletionSource receipt = expected.GetOrAdd(nonce,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            await receipt.Task.WaitAsync(timeout.Token);
            return false;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return !HasReceived(nonce);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        if (connection.State != HubConnectionState.Disconnected)
            await connection.StopAsync(timeout.Token);
        await connection.DisposeAsync();
    }
}

internal sealed class RealtimeAcceptanceApiProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Process process;
    private readonly Task<string> standardError;
    private readonly SemaphoreSlim commandLock = new(1, 1);
    private bool disposed;

    private RealtimeAcceptanceApiProcess(Process process, Task<string> standardError, RealtimeApiReady ready)
    {
        this.process = process;
        this.standardError = standardError;
        Ready = ready;
    }

    public RealtimeApiReady Ready { get; }
    public string Address => Ready.Address;
    public int ProcessId => Ready.ProcessId;
    public RuntimeRoleNames Roles => Ready.Roles;

    public static async Task<RealtimeAcceptanceApiProcess> StartAsync(string instance,
        IReadOnlyDictionary<string, string?> settings, string certificateThumbprint)
    {
        ProcessStartInfo start = CreateStartInfo(instance, settings);
        Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Realtime acceptance API process did not start.");
        Task<string> standardError = ReadBoundedAsync(process.StandardError, 8_192);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(45));
            RealtimeApiReady? ready = null;
            int consumed = 0;
            for (int lineNumber = 0; lineNumber < 32 && consumed < 16_384 && ready is null; lineNumber++)
            {
                BoundedProcessLine line;
                try { line = await ReadBoundedLineAsync(process.StandardOutput, 4_096, timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Realtime acceptance API process '{instance}' did not report readiness within 45 seconds.");
                }
                if (line.EndOfStream)
                {
                    await process.WaitForExitAsync(timeout.Token);
                    _ = await standardError.WaitAsync(timeout.Token);
                    throw new InvalidOperationException(
                        $"Realtime acceptance API process '{instance}' exited before readiness with code {process.ExitCode}.");
                }
                consumed += line.Text.Length;
                if (line.Oversized || !line.Text.StartsWith('{')) continue;
                try
                {
                    RealtimeApiReady? candidate = JsonSerializer.Deserialize<RealtimeApiReady>(line.Text, JsonOptions);
                    if (candidate?.Kind == "realtime-ready") ready = candidate;
                }
                catch (JsonException) { }
            }
            if (ready is null)
                throw new InvalidOperationException("Realtime acceptance API process returned no bounded readiness metadata.");
            if (ready.Kind != "realtime-ready" || ready.ProcessId != process.Id || ready.Instance != instance
                || !Uri.TryCreate(ready.Address, UriKind.Absolute, out Uri? address) || address.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Realtime acceptance API process returned mismatched readiness metadata.");
            _ = certificateThumbprint;
            return new(process, standardError, ready);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(cleanup.Token); } catch (OperationCanceledException) { }
            try { _ = await standardError.WaitAsync(cleanup.Token); } catch (OperationCanceledException) { }
            process.Dispose();
            throw;
        }
    }

    public async Task<string> SendProbeAsync(string connectionId, Guid nonce,
        TimeSpan commandTimeout, CancellationToken cancellationToken = default)
    {
        if (connectionId.Length is < 1 or > 128 || nonce == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(connectionId));
        await commandLock.WaitAsync(cancellationToken);
        try
        {
            if (process.HasExited)
                throw new InvalidOperationException("Realtime acceptance API process exited before a probe command.");
            string command = JsonSerializer.Serialize(new { action = "send-probe", connectionId, nonce = nonce.ToString("D") }, JsonOptions);
            if (command.Length > 4_096) throw new InvalidOperationException("Realtime acceptance command exceeded its bound.");
            await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(commandTimeout);
            string? line;
            try { line = await process.StandardOutput.ReadLineAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Realtime acceptance API command did not return within its bound.");
            }
            if (line is null || line.Length > 4_096)
                throw new InvalidOperationException("Realtime acceptance API returned no bounded command result.");
            RealtimeControlResponse result = JsonSerializer.Deserialize<RealtimeControlResponse>(line, JsonOptions)
                ?? throw new InvalidOperationException("Realtime acceptance API returned invalid command metadata.");
            if (result.Nonce != nonce.ToString("D") || result.Outcome is not ("send-completed" or "send-failed"))
                throw new InvalidOperationException("Realtime acceptance API returned mismatched command metadata.");
            return result.Outcome;
        }
        finally { commandLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        process.StandardInput.Close();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            _ = await standardError.WaitAsync(timeout.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Realtime acceptance API process exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
            _ = await standardError.WaitAsync(cleanup.Token);
            throw new TimeoutException("Realtime acceptance API process did not stop after stdin closed.");
        }
        finally
        {
            process.Dispose();
            commandLock.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string instance, IReadOnlyDictionary<string, string?> settings)
    {
        string testAssembly = Assembly.GetExecutingAssembly().Location;
        string helperAssembly = typeof(ExecutionAcceptanceProcessMarker).Assembly.Location;
        string dotnet = ResolveDotnetHost();
        ProcessStartInfo start = new(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
        start.ArgumentList.Add(helperAssembly);
        start.ArgumentList.Add("realtime-host");
        Dictionary<string, string?> inherited = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string key
                && (key is "PATH" or "HOME" or "TMPDIR" or "TMP" or "TEMP"
                    || key.StartsWith("DOTNET_", StringComparison.Ordinal)))
            .ToDictionary(entry => (string)entry.Key!, entry => (string?)entry.Value, StringComparer.Ordinal);
        start.Environment.Clear();
        foreach ((string name, string? value) in inherited)
            if (value is not null) start.Environment[name] = value;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["TRYKATCH_ACCEPTANCE_INSTANCE"] = instance;
        foreach ((string name, string? value) in settings)
            if (value is not null) start.Environment[name] = value;
        return start;
    }

    private static string ResolveDotnetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root) && File.Exists(Path.Combine(root, "dotnet")))
            return Path.Combine(root, "dotnet");
        return "dotnet";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        char[] buffer = new char[512];
        System.Text.StringBuilder result = new(Math.Min(maximumCharacters, 512));
        while (true)
        {
            int read = await reader.ReadAsync(buffer);
            if (read == 0) return result.ToString();
            int remaining = maximumCharacters - result.Length;
            if (remaining > 0) result.Append(buffer, 0, Math.Min(read, remaining));
        }
    }

    private static async Task<BoundedProcessLine> ReadBoundedLineAsync(StreamReader reader, int maximumCharacters,
        CancellationToken cancellationToken)
    {
        char[] character = new char[1];
        System.Text.StringBuilder line = new(Math.Min(maximumCharacters, 512));
        bool oversized = false;
        while (true)
        {
            int read = await reader.ReadAsync(character.AsMemory(), cancellationToken);
            if (read == 0) return new(line.ToString(), oversized, EndOfStream: true);
            if (character[0] == '\n') return new(line.ToString(), oversized, EndOfStream: false);
            if (character[0] == '\r') continue;
            if (line.Length < maximumCharacters) line.Append(character[0]);
            else oversized = true;
        }
    }
}

internal sealed record BoundedProcessLine(string Text, bool Oversized, bool EndOfStream);
