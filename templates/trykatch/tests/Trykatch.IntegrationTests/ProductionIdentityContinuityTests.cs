using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Trykatch.Identity;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass, DoNotParallelize]
public sealed class ProductionIdentityContinuityTests
{
    [TestMethod]
    [DataRow("same-id")]
    [DataRow("same-id-ciphertext")]
    [DataRow("plaintext")]
    [DataRow("malformed")]
    [DataRow("xml")]
    [DataRow("dtd")]
    [DataRow("deserializer")]
    [DataRow("decryptor")]
    public async Task EveryKeyRefreshRejectsUnsafeStoredXmlBeforeActivation(string mutation)
    {
        using IdentityCertificateFixture certificates = new();
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        XElement plaintext = XElement.Parse(await ReadKeysAsync(host.OwnerConnection));
        Dictionary<string, string?> settings = ProductionSettings(certificates);
        await MaintenanceAsync(host.OwnerConnection, settings, "apply");
        await host.RestartAsync(settings);
        IKeyManager manager = host.Services.GetRequiredService<IKeyManager>();
        foreach (IKey key in manager.GetAllKeys()) key.CreateEncryptor().ShouldNotBeNull();
        XElement changed = mutation == "plaintext" ? plaintext : XElement.Parse(await ReadKeysAsync(host.OwnerConnection));
        if (!mutation.StartsWith("same-id", StringComparison.Ordinal)) changed.SetAttributeValue("id", Guid.NewGuid());
        if (mutation is "same-id" or "malformed") changed.SetAttributeValue("version", "99");
        if (mutation == "same-id-ciphertext") changed.Descendants(XName.Get("CipherValue", "http://www.w3.org/2001/04/xmlenc#")).Last().Value = "invalid-fixture-ciphertext";
        if (mutation == "deserializer") changed.Element("descriptor")!.SetAttributeValue("deserializerType", typeof(UnsafeDescriptorProbe).AssemblyQualifiedName);
        if (mutation == "decryptor") changed.Descendants(XName.Get("encryptedSecret", "http://schemas.asp.net/2015/03/dataProtection")).Single()
            .SetAttributeValue("decryptorType", typeof(UnsafeDecryptorProbe).AssemblyQualifiedName);
        string sql = mutation.StartsWith("same-id", StringComparison.Ordinal) ? "UPDATE identity.data_protection_keys SET \"Xml\" = @xml" :
            "INSERT INTO identity.data_protection_keys (\"FriendlyName\", \"Xml\") VALUES ('unsafe-fixture', @xml)";
        string xml = mutation switch
        {
            "xml" => "<key unclosed-fixture",
            "dtd" => "<!DOCTYPE key [<!ENTITY fixture 'forbidden'>]><key>&fixture;</key>",
            _ => changed.ToString()
        };
        await ExecuteAsync(host.OwnerConnection, sql, xml);
        UnsafeDescriptorProbe.Activations = 0;
        UnsafeDecryptorProbe.Activations = 0;
        Should.Throw<InvalidOperationException>(() =>
        {
            foreach (IKey key in manager.GetAllKeys()) _ = key.CreateEncryptor();
        });
        UnsafeDescriptorProbe.Activations.ShouldBe(0);
        UnsafeDecryptorProbe.Activations.ShouldBe(0);
    }

    [TestMethod]
    public async Task RepositoryRejectsPlaintextWritesAndPreservesEncryptedKeyAndRevocationWrites()
    {
        using IdentityCertificateFixture certificates = new();
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        XElement plaintext = XElement.Parse(await ReadKeysAsync(host.OwnerConnection));
        Dictionary<string, string?> settings = ProductionSettings(certificates);
        await MaintenanceAsync(host.OwnerConnection, settings, "apply");
        await host.RestartAsync(settings);
        string encrypted = await ReadKeysAsync(host.OwnerConnection);
        var repository = host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository!;
        Should.Throw<InvalidOperationException>(() => repository.StoreElement(plaintext, "plaintext-fixture")).Message.ShouldContain("key ring");
        (await ReadKeysAsync(host.OwnerConnection)).ShouldBe(encrypted);
        IKeyManager manager = host.Services.GetRequiredService<IKeyManager>();
        IKey created = manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        manager.RevokeKey(created.KeyId, "fixture revocation");
        manager.GetAllKeys().Single(key => key.KeyId == created.KeyId).IsRevoked.ShouldBeTrue();
        string written = await ReadKeysAsync(host.OwnerConnection);
        written.ShouldContain("encryptedSecret");
        written.ShouldContain("<revocation");
        written.ShouldNotContain("<masterKey");
    }

    [TestMethod]
    public async Task ConcurrentValidInsertionDoesNotCompareDifferentRepositorySnapshots()
    {
        using IdentityCertificateFixture certificates = new();
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        Dictionary<string, string?> settings = ProductionSettings(certificates);
        await MaintenanceAsync(host.OwnerConnection, settings, "apply");
        XElement additional = XElement.Parse(await ReadKeysAsync(host.OwnerConnection));
        additional.SetAttributeValue("id", Guid.NewGuid());
        InsertAfterKeyRead interceptor = new(host.OwnerConnection, additional.ToString());
        await host.RestartAsync(settings, configureServices: services => services.AddDbContext<IdentityDbContext>(options => options.AddInterceptors(interceptor)));
        using HttpClient restarted = host.CreateClient();
        interceptor.Inserted.ShouldBeTrue();
        host.Services.GetRequiredService<IKeyManager>().GetAllKeys().Count.ShouldBe(2);
    }

    public sealed class UnsafeDescriptorProbe : IAuthenticatedEncryptorDescriptorDeserializer
    {
        internal static int Activations;
        public UnsafeDescriptorProbe() => Interlocked.Increment(ref Activations);
        public IAuthenticatedEncryptorDescriptor ImportFromXml(XElement element) => throw new InvalidOperationException("Unsafe descriptor fixture was activated.");
    }

    public sealed class UnsafeDecryptorProbe : IXmlDecryptor
    {
        internal static int Activations;
        public UnsafeDecryptorProbe() => Interlocked.Increment(ref Activations);
        public XElement Decrypt(XElement encryptedElement) => throw new InvalidOperationException("Unsafe decryptor fixture was activated.");
    }

    private sealed class InsertAfterKeyRead(string connectionString, string xml) : DbCommandInterceptor
    {
        private int inserted;
        public bool Inserted => Volatile.Read(ref inserted) != 0;

        private DbDataReader Insert(DbCommand command, DbDataReader result)
        {
            if (command.CommandText.Contains("data_protection_keys", StringComparison.Ordinal) && Interlocked.Exchange(ref inserted, 1) == 0)
            {
                using NpgsqlConnection connection = new(connectionString);
                connection.Open();
                using NpgsqlCommand insert = new("INSERT INTO identity.data_protection_keys (\"FriendlyName\", \"Xml\") VALUES ('concurrent-fixture', @xml)", connection);
                insert.Parameters.AddWithValue("xml", xml);
                insert.ExecuteNonQuery();
            }
            return result;
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) => Insert(command, result);
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default) => ValueTask.FromResult(Insert(command, result));
    }

    [TestMethod]
    public async Task ExplicitMaintenanceDryRunAndApplyPreserveCookieAntiforgeryAndPendingMfa()
    {
        using IdentityCertificateFixture certificates = new();
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = host.CreateClient();
        using HttpResponseMessage csrfResponse = await client.GetAsync("/api/v1/auth/antiforgery");
        string csrfCookie = csrfResponse.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        using HttpResponseMessage login = await PostAsync(client, "/api/v1/auth/login", new { email = SecurityHost.Email, password = SecurityHost.Password, rememberMe = false });
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        string cookie = login.Headers.GetValues("Set-Cookie").First(value => value.StartsWith("__Host-trykatch=", StringComparison.Ordinal)).Split(';')[0] + "; " + csrfCookie;
        using HttpResponseMessage grantResponse = await PostAsync(client, "/reauthenticate", new { purpose = "mfa.enroll", password = SecurityHost.Password });
        string grant = (await grantResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("grant").GetString()!;
        using HttpResponseMessage setupResponse = await PostAsync(client, "/mfa/setup", new { grant });
        JsonElement setup = await setupResponse.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
        string oldXml = await ReadKeysAsync(host.OwnerConnection);
        Dictionary<string, string?> settings = ProductionSettings(certificates);
        (await InvokeMaintenanceAsync(host.OwnerConnection, settings, "apply", confirmBackup: false)).Exit.ShouldBe(1);
        JsonElement preview = await MaintenanceAsync(host.OwnerConnection, settings, "dry-run");
        preview.GetProperty("updated").GetInt32().ShouldBe(0);
        (await ReadKeysAsync(host.OwnerConnection)).ShouldBe(oldXml);
        JsonElement applied = await MaintenanceAsync(host.OwnerConnection, settings, "apply");
        applied.GetProperty("updated").GetInt32().ShouldBeGreaterThan(0);
        string encrypted = await ReadKeysAsync(host.OwnerConnection);
        encrypted.ShouldContain("encryptedSecret");
        encrypted.ShouldNotContain("<masterKey");
        (await MaintenanceAsync(host.OwnerConnection, settings, "apply")).GetProperty("updated").GetInt32().ShouldBe(0);
        await host.RestartAsync(settings);
        using HttpClient restarted = host.CreateClient(cookie);
        (await restarted.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
        settings["DataProtection:DecryptionCertificates:0:Path"] = settings["DataProtection:Certificate:Path"];
        settings["DataProtection:DecryptionCertificates:0:Password"] = IdentityCertificateFixture.Password;
        settings["DataProtection:Certificate:Path"] = certificates.Create("rotated");
        await host.RestartAsync(settings);
        using HttpClient rotated = host.CreateClient(cookie);
        (await rotated.GetAsync("/api/v1/auth/session")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpRequestMessage confirm = new(HttpMethod.Post, "/api/v1/account/security/mfa/enable")
        {
            Content = JsonContent.Create(new { enrollmentId = setup.GetProperty("enrollmentId").GetGuid(), code = Totp(setup.GetProperty("sharedKey").GetString()!) })
        };
        confirm.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        using HttpResponseMessage result = await rotated.SendAsync(confirm);
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codes").GetArrayLength().ShouldBe(10);
    }

    [TestMethod]
    public async Task RotationEncryptsNewKeysWithBAndRequiresRetainedAIncludingRestoredBackups()
    {
        using IdentityCertificateFixture certificates = new();
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        Dictionary<string, string?> settings = ProductionSettings(certificates);
        await MaintenanceAsync(host.OwnerConnection, settings, "apply");
        await host.RestartAsync(settings);
        string protectedWithA = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("rotation-test").Protect("retained payload");
        string backup = await ReadKeysAsync(host.OwnerConnection);
        settings["DataProtection:DecryptionCertificates:0:Path"] = settings["DataProtection:Certificate:Path"];
        settings["DataProtection:DecryptionCertificates:0:Password"] = IdentityCertificateFixture.Password;
        settings["DataProtection:Certificate:Path"] = certificates.Create("b");
        await host.RestartAsync(settings);
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("rotation-test").Unprotect(protectedWithA).ShouldBe("retained payload");
        host.Services.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        (await MaintenanceAsync(host.OwnerConnection, settings, "dry-run")).GetProperty("keys").GetInt32().ShouldBe(2);
        Dictionary<string, string?> withoutB = new(settings) { ["DataProtection:Certificate:Path"] = settings["DataProtection:DecryptionCertificates:0:Path"] };
        (await InvokeMaintenanceAsync(host.OwnerConnection, withoutB, "dry-run")).Exit.ShouldBe(1);
        Dictionary<string, string?> withoutA = new(settings) { ["DataProtection:DecryptionCertificates:0:Path"] = settings["DataProtection:Certificate:Path"] };
        (await InvokeMaintenanceAsync(host.OwnerConnection, withoutA, "dry-run")).Exit.ShouldBe(1);
        await host.RestartAsync(withoutA);
        Should.Throw<InvalidOperationException>(() => host.CreateClient()).Message.ShouldContain("key ring");
        // Restore the original encrypted backup with its original key ID, as an operator would.
        await ExecuteAsync(host.OwnerConnection, "DELETE FROM identity.data_protection_keys; INSERT INTO identity.data_protection_keys (\"FriendlyName\", \"Xml\") VALUES ('restored', @xml)", backup);
        await host.RestartAsync(settings);
        host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("rotation-test").Unprotect(protectedWithA).ShouldBe("retained payload");
    }

    [TestMethod]
    public async Task MaintenanceFailsClosedAndRollsBackOnMalformedRowsAndDatabaseWriteFailures()
    {
        using IdentityCertificateFixture certificates = new();
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient client = await host.SignInAsync();
        host.Services.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        Dictionary<string, string?> settings = ProductionSettings(certificates);
        await ExecuteAsync(host.OwnerConnection, "INSERT INTO identity.data_protection_keys (\"FriendlyName\", \"Xml\") VALUES ('malformed', '<key version=\"99\"/>')");
        string malformed = await ReadKeysAsync(host.OwnerConnection);
        var invalid = await InvokeMaintenanceAsync(host.OwnerConnection, settings, "apply");
        invalid.Exit.ShouldBe(1);
        invalid.Error.ShouldContain("Key maintenance failed");
        invalid.Error.ShouldNotContain("<key");
        (await ReadKeysAsync(host.OwnerConnection)).ShouldBe(malformed);
        await ExecuteAsync(host.OwnerConnection, "DELETE FROM identity.data_protection_keys WHERE \"FriendlyName\" = 'malformed'");
        string before = await ReadKeysAsync(host.OwnerConnection);
        await ExecuteAsync(host.OwnerConnection, """
            CREATE FUNCTION identity.reject_second_key_update() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF OLD."Id" = (SELECT max("Id") FROM identity.data_protection_keys) THEN RAISE EXCEPTION 'fixture rejects second write'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_second_key_update BEFORE UPDATE ON identity.data_protection_keys FOR EACH ROW EXECUTE FUNCTION identity.reject_second_key_update();
            """);
        (await InvokeMaintenanceAsync(host.OwnerConnection, settings, "apply")).Exit.ShouldBe(1);
        (await ReadKeysAsync(host.OwnerConnection)).ShouldBe(before);
        await ExecuteAsync(host.OwnerConnection, "DROP TRIGGER reject_second_key_update ON identity.data_protection_keys; DROP FUNCTION identity.reject_second_key_update()");
        JsonElement[] concurrent = await Task.WhenAll(MaintenanceAsync(host.OwnerConnection, settings, "apply"), MaintenanceAsync(host.OwnerConnection, settings, "apply"));
        concurrent.Select(result => result.GetProperty("updated").GetInt32()).Order().ShouldBe([0, 2]);
        NpgsqlConnectionStringBuilder runtime = new(host.OwnerConnection) { Username = "trykatch_identity_runtime", Password = "IdentityRuntime!Pass123" };
        // This fixture owns the disposable test roles. A valid runtime credential must
        // still fail the maintenance ownership check, not merely authentication.
        await ExecuteAsync(host.OwnerConnection, "ALTER ROLE trykatch_identity_runtime PASSWORD 'IdentityRuntime!Pass123'");
        (await InvokeMaintenanceAsync(runtime.ConnectionString, settings, "apply")).Exit.ShouldBe(1);
    }

    [TestMethod]
    public async Task ProductionRejectsAnUnmigratedPlaintextKeyRing()
    {
        using IdentityCertificateFixture certificates = new();
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient oldClient = await host.SignInAsync();
        (await ReadKeysAsync(host.OwnerConnection)).ShouldContain("masterKey");
        await host.RestartAsync(ProductionSettings(certificates));
        InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => host.CreateClient());
        failure.Message.ShouldContain("key ring");
    }

    private static Dictionary<string, string?> ProductionSettings(IdentityCertificateFixture certificates)
    {
        Dictionary<string, string?> settings = certificates.Settings();
        settings.Remove("ConnectionStrings:trykatch-identity");
        settings["DataProtection:Certificate:Path"] = certificates.Create("active");
        settings["DataProtection:Certificate:Password"] = IdentityCertificateFixture.Password;
        string passwordFile = Path.Combine(certificates.DirectoryPath, "certificate-password");
        File.WriteAllText(passwordFile, IdentityCertificateFixture.Password + "\n");
        settings["OpenIddict:SigningCertificate:Password"] = null;
        settings["OpenIddict:SigningCertificate:PasswordFile"] = passwordFile;
        settings["Email:Host"] = "localhost";
        settings["Email:Port"] = "2525";
        settings["Email:From"] = "test@example.test";
        settings["Email:Security"] = "StartTls";
        return settings;
    }

    private static async Task<string> ReadKeysAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT string_agg(\"Xml\", E'\\n' ORDER BY \"Id\") FROM identity.data_protection_keys", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<JsonElement> MaintenanceAsync(string connectionString, IReadOnlyDictionary<string, string?> settings, string mode)
    {
        var result = await InvokeMaintenanceAsync(connectionString, settings, mode);
        result.Exit.ShouldBe(0, result.Error);
        return JsonDocument.Parse(result.Output).RootElement.Clone();
    }

    private static async Task ExecuteAsync(string connectionString, string sql, string? xml = null)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        if (xml is not null) command.Parameters.AddWithValue("xml", xml);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(int Exit, string Output, string Error)> InvokeMaintenanceAsync(string connectionString, IReadOnlyDictionary<string, string?> settings, string mode, bool confirmBackup = true)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Trykatch.slnx"))) root = root.Parent;
        root.ShouldNotBeNull();
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        string configuration = typeof(ProductionIdentityContinuityTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        start.ArgumentList.Add(Path.Combine(root!.FullName, $"src/API/Trykatch.Migrator/bin/{configuration}/net10.0/Trykatch.Migrator.dll"));
        start.ArgumentList.Add("--data-protection-keys");
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add("--confirm-key-backup");
        start.ArgumentList.Add(confirmBackup ? "true" : "false");
        start.Environment["ConnectionStrings__trykatchdb"] = connectionString;
        foreach ((string key, string? value) in settings.Where(pair => pair.Key.StartsWith("DataProtection:", StringComparison.Ordinal)))
            start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = value;
        using Process process = Process.Start(start)!;
        return await ProcessExecution.WaitAsync(process, TimeSpan.FromSeconds(30));
    }
}
