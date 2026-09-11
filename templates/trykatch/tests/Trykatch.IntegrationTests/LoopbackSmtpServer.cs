#if TRYKATCH_EMAIL
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit.Net.Smtp;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.IntegrationTests;

/// <summary>A real, test-only SMTP/TLS endpoint. Trust is scoped to the returned MailKit client.</summary>
internal sealed class LoopbackSmtpServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
    private readonly X509Certificate2 root;
    private readonly X509Certificate2 server;
    private readonly Task run;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool ReceivedMessage { get; private set; }
    public bool Authenticated { get; private set; }
    public bool UsedTls { get; private set; }

    public LoopbackSmtpServer(bool implicitTls = false, bool advertiseStartTls = true, bool wrongName = false, bool expired = false)
    {
        using RSA rootKey = RSA.Create(2048);
        CertificateRequest ca = new("CN=Trykatch fixture-only root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        ca.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        ca.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        root = ca.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(3));
        using RSA serverKey = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(wrongName ? "not-localhost.example.test" : "localhost");
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1), RandomNumberGenerator.GetBytes(16));
        server = issued.CopyWithPrivateKey(serverKey);
        listener.Start();
        run = RunAsync(implicitTls, advertiseStartTls);
    }

    public ISmtpClientFactory TrustedClientFactory() => new FixtureClientFactory(root);

    private sealed class FixtureClientFactory(X509Certificate2 root) : ISmtpClientFactory
    {
        public SmtpClient CreateClient()
        {
            SmtpClient client = new() { Timeout = 10_000 };
            client.ServerCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
                using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                using X509Chain chain = new();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Fixture root has no online revocation service.
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(leaf);
            };
            return client;
        }
    }

    private async Task RunAsync(bool implicitTls, bool advertiseStartTls)
    {
        try
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(stop.Token);
            Stream stream = connection.GetStream();
            if (implicitTls) stream = await TlsAsync(stream);
            StreamReader reader = Reader(stream);
            StreamWriter writer = Writer(stream);
            try
            {
                await writer.WriteLineAsync("220 localhost fixture SMTP");
                while (await reader.ReadLineAsync(stop.Token) is { } command)
                {
                    if (command.StartsWith("EHLO ", StringComparison.Ordinal))
                    {
                        await writer.WriteLineAsync("250-localhost");
                        if (!UsedTls && advertiseStartTls) await writer.WriteLineAsync("250-STARTTLS");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                    }
                    else if (command == "STARTTLS")
                    {
                        await writer.WriteLineAsync("220 begin TLS");
                        reader.Dispose();
                        await writer.DisposeAsync();
                        stream = await TlsAsync(stream);
                        reader = Reader(stream);
                        writer = Writer(stream);
                    }
                    else if (command.StartsWith("AUTH PLAIN", StringComparison.Ordinal))
                    {
                        string value = command.Length > 11 ? command[11..] : "";
                        if (value.Length == 0) { await writer.WriteLineAsync("334 "); value = await reader.ReadLineAsync(stop.Token) ?? ""; }
                        string credentials = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                        Authenticated = credentials.EndsWith("\0fixture-user\0fixture-password", StringComparison.Ordinal);
                        await writer.WriteLineAsync(Authenticated ? "235 authenticated" : "535 fixture authentication denied");
                    }
                    else if (command.StartsWith("MAIL FROM:", StringComparison.Ordinal) || command.StartsWith("RCPT TO:", StringComparison.Ordinal))
                        await writer.WriteLineAsync("250 accepted");
                    else if (command == "DATA")
                    {
                        await writer.WriteLineAsync("354 send message");
                        while (await reader.ReadLineAsync(stop.Token) is { } line && line != ".") { }
                        ReceivedMessage = true;
                        await writer.WriteLineAsync("250 queued");
                    }
                    else if (command == "QUIT") { await writer.WriteLineAsync("221 goodbye"); break; }
                    else await writer.WriteLineAsync("250 accepted");
                }
            }
            finally { reader.Dispose(); await writer.DisposeAsync(); await stream.DisposeAsync(); }
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or AuthenticationException or SocketException)
        {
            // An expected client certificate/authentication rejection may end the connection.
        }
    }

    private async Task<Stream> TlsAsync(Stream stream)
    {
        SslStream tls = new(stream, false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = server,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, stop.Token);
            UsedTls = true;
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }

    private static StreamReader Reader(Stream stream) => new(stream, Encoding.ASCII, false, 1024, true);
    private static StreamWriter Writer(Stream stream) => new(stream, Encoding.ASCII, 1024, true) { AutoFlush = true, NewLine = "\r\n" };

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        await run;
        stop.Dispose();
        server.Dispose();
        root.Dispose();
    }
}
#endif
