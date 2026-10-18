using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Ddd.Windows.Tests;

public sealed class TlsFeasibilityTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(8);
    private static readonly byte[] Payload = "DDD test-only application payload"u8.ToArray();

    [Fact]
    public async Task ExactCertificateDerPinAllowsSelfSignedTlsAndPayload()
    {
        var result = await ExchangeAsync(mismatch: false);

        Assert.True(result.PinChecked, HandshakeDiagnostics(result));
        Assert.Null(result.ClientError);
        Assert.Null(result.Server.Error);
        Assert.True(result.Server.Authenticated);
        Assert.True(result.PayloadWritten);
        Assert.Equal(Payload, result.Server.Payload);
    }

    [Fact]
    public async Task MismatchedCertificateDerPinRejectsTlsBeforeApplicationPayload()
    {
        var result = await ExchangeAsync(mismatch: true);

        Assert.True(result.PinChecked, HandshakeDiagnostics(result));
        Assert.IsType<AuthenticationException>(result.ClientError);
        Assert.False(result.PayloadWritten);
        Assert.Empty(result.Server.Payload);
        // TLS versions/backends differ on whether the server sees the rejection
        // during authentication or its first read. Neither may deliver plaintext.
        Assert.True(result.Server.Error is AuthenticationException or IOException,
            "The server must observe peer rejection/closure, not the test deadline.");
    }

    [Fact]
    public async Task SilentPeerHandshakeIsCancelledByDeadline()
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpClient();
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var connect = client.ConnectAsync(endpoint.Address, endpoint.Port, deadline.Token);
            using var silentPeer = await listener.AcceptTcpClientAsync(deadline.Token);
            await connect;
            using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(1));

            // The accepted peer deliberately sends no TLS response. No background
            // server task is needed; both sockets are owned and disposed here.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, handshakeDeadline.Token));

            Assert.True(handshakeDeadline.IsCancellationRequested);
            Assert.False(deadline.IsCancellationRequested);
            Assert.False(tls.IsAuthenticated);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<ExchangeResult> ExchangeAsync(bool mismatch)
    {
        // A fresh TLS-only key, never the long-term Git signing identity.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        var now = DateTimeOffset.UtcNow;
        using var originalCertificate = request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(10));
                using var windowsCertificate = OperatingSystem.IsWindows()
                    ? ImportForSchannel(originalCertificate)
                    : null;
                var certificate = windowsCertificate ?? originalCertificate;
        byte[] expectedPin = SHA256.HashData(certificate.RawData);
        if (mismatch) expectedPin[0] ^= 1;

        using var deadline = new CancellationTokenSource(TestDeadline);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var client = new TcpClient();
        TcpClient? peer = null;
        Task<ServerResult>? serverTask = null;
        bool pinChecked = false;
        bool payloadWritten = false;
        listener.Start();
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var connect = client.ConnectAsync(endpoint.Address, endpoint.Port, deadline.Token);
            peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await connect;
            serverTask = ReceiveAsync(peer, certificate, deadline.Token);
            using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                (_, presentedCertificate, _, _) =>
                {
                    pinChecked = true;
                    return presentedCertificate is not null && CryptographicOperations.FixedTimeEquals(
                        expectedPin, SHA256.HashData(presentedCertificate.GetRawCertData()));
                });

            Exception? clientError = await Record.ExceptionAsync(async () =>
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, deadline.Token);
                await tls.WriteAsync(Payload, deadline.Token);
                payloadWritten = true;
            });
            // Close even after failed authentication so the server observes the
            // alert/EOF without relying on the outer cancellation deadline.
            tls.Dispose();
            client.Dispose();
            var server = await serverTask;
            Assert.False(deadline.IsCancellationRequested);
            return new ExchangeResult(pinChecked, payloadWritten, clientError, server);
        }
        finally
        {
            deadline.Cancel();
            client.Dispose();
            peer?.Dispose();
            listener.Stop();
            // Observe all server outcomes before disposing the certificate/key.
            if (serverTask is not null) await serverTask.WaitAsync(TestDeadline);
        }
    }

    private static string HandshakeDiagnostics(ExchangeResult result) =>
        $"Certificate pin callback checked: {result.PinChecked}; server authenticated: {result.Server.Authenticated}." +
        $"{Environment.NewLine}Client exception: {result.ClientError?.ToString() ?? "<none>"}" +
        $"{Environment.NewLine}Server exception: {result.Server.Error?.ToString() ?? "<none>"}";

    private static X509Certificate2 ImportForSchannel(X509Certificate2 certificate)
    {
        // Schannel may reject ephemeral keys. DefaultKeySet permits OS temporary
        // key storage for this short-lived test-only TLS key, never a Git key.
        byte[] pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            return X509CertificateLoader.LoadPkcs12(pfx, password: null,
                keyStorageFlags: X509KeyStorageFlags.DefaultKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static async Task<ServerResult> ReceiveAsync(
        TcpClient peer, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        using var tls = new SslStream(peer.GetStream(), leaveInnerStreamOpen: false);
        bool authenticated = false;
        using var received = new MemoryStream();
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            authenticated = true;
            byte[] buffer = new byte[Payload.Length];
            while (received.Length < Payload.Length)
            {
                int count = await tls.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                    return new ServerResult(authenticated, received.ToArray(), new IOException("Peer closed before payload."));
                received.Write(buffer, 0, count);
            }
            return new ServerResult(authenticated, received.ToArray(), null);
        }
        catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException)
        {
            return new ServerResult(authenticated, received.ToArray(), error);
        }
    }

    private sealed record ServerResult(bool Authenticated, byte[] Payload, Exception? Error);
    private sealed record ExchangeResult(bool PinChecked, bool PayloadWritten, Exception? ClientError, ServerResult Server);
}
