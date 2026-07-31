using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using S3Explorer.Core;
using S3Explorer.Infrastructure.Cdn;
using Xunit;

namespace S3Explorer.Infrastructure.Cdn.Tests;

public sealed class CdnInfrastructureTests
{
    [Fact]
    public async Task ConfigurationStoreRoundTripsProfilesAndBindings()
    {
        var path = TemporaryFile("cdn-config.json");
        try
        {
            var profile = new CdnProfile
            {
                Name = "site",
                BaseUrl = "https://cdn.example",
                Notes = "生产站点 CDN，变更前联系值班人员。"
            };
            var binding = new CdnBinding
            {
                StorageProfileId = Guid.NewGuid(),
                Bucket = "site",
                SourcePrefix = "assets/",
                CdnProfileId = profile.Id,
                CdnPathPrefix = "static/"
            };
            var store = new JsonCdnConfigurationStore(path);

            await store.SaveAsync(
                new CdnConfiguration([profile], [binding]));
            var loaded = await store.LoadAsync();

            Assert.Equal(profile, Assert.Single(loaded.Profiles));
            Assert.Equal(binding, Assert.Single(loaded.Bindings));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task ConfigurationStorePersistsLastCertificateCheck()
    {
        var path = TemporaryFile("cdn-config.json");
        try
        {
            var checkedAt = DateTimeOffset.Parse("2026-07-29T12:00:00Z");
            var result = new CdnCertificateCheckResult(
                new Uri("https://cdn.example.com"),
                checkedAt,
                checkedAt.AddDays(-30),
                checkedAt.AddDays(60),
                "CN=cdn.example.com",
                "CN=Example CA",
                new string('A', 64),
                "Tls13",
                CdnCertificateProblems.None,
                []);
            var profile = new CdnProfile
            {
                Name = "site",
                BaseUrl = "https://cdn.example.com",
                LastCertificateCheck = result
            };
            var store = new JsonCdnConfigurationStore(path);

            await store.SaveAsync(new CdnConfiguration([profile], []));
            var loaded = Assert.Single((await store.LoadAsync()).Profiles).LastCertificateCheck;

            Assert.NotNull(loaded);
            Assert.Equal(result.Endpoint, loaded.Endpoint);
            Assert.Equal(result.CheckedAt, loaded.CheckedAt);
            Assert.Equal(result.NotAfter, loaded.NotAfter);
            Assert.Equal(result.StatusText, loaded.StatusText);
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public void ValidatorRejectsCertificateResultFromAnotherEndpoint()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new CdnProfile
        {
            Name = "site",
            BaseUrl = "https://cdn.example.com",
            LastCertificateCheck = new CdnCertificateCheckResult(
                new Uri("https://other.example.com"),
                now,
                now.AddDays(-1),
                now.AddDays(30),
                "CN=other.example.com",
                "CN=CA",
                new string('A', 64),
                "Tls13",
                CdnCertificateProblems.None,
                [])
        };

        var error = Assert.Single(CdnConfigurationValidator.Validate(new CdnConfiguration([profile], [])));

        Assert.Contains("证书检测结果无效", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialStoreNeverWritesPlaintextSecret()
    {
        var path = TemporaryFile("cdn-credentials.json");
        try
        {
            var store = new JsonCdnCredentialStore(new TestProtector(), path);
            var credential = new CdnCredential
            {
                Name = "purge token",
                AuthenticationType = CdnAuthenticationType.BearerToken,
                Secret = "do-not-store-in-plaintext"
            };

            await store.SaveAsync([credential]);
            var json = await File.ReadAllTextAsync(path);
            var loaded = await store.LoadAsync();

            Assert.DoesNotContain(
                credential.Secret,
                json,
                StringComparison.Ordinal);
            Assert.Equal(credential, Assert.Single(loaded));
        }
        finally
        {
            DeleteDirectory(path);
        }
    }

    [Fact]
    public async Task StoresRejectUnknownDocumentVersions()
    {
        var configurationPath = TemporaryFile("cdn-config.json");
        var credentialPath = Path.Combine(
            Path.GetDirectoryName(configurationPath)!,
            "cdn-credentials.json");
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                "{\"version\":2,\"profiles\":[],\"bindings\":[]}");
            await File.WriteAllTextAsync(
                credentialPath,
                "{\"version\":2,\"credentials\":[]}");

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new JsonCdnConfigurationStore(configurationPath).LoadAsync());
            await Assert.ThrowsAsync<InvalidDataException>(
                () => new JsonCdnCredentialStore(new TestProtector(), credentialPath).LoadAsync());
        }
        finally
        {
            DeleteDirectory(configurationPath);
        }
    }

    [Fact]
    public async Task ProbeUsesRangeAndReportsCacheHeaders()
    {
        CapturedRequest? captured = null;
        var service = new GenericHttpCdnDeliveryService(
            _ => new StubHandler(request =>
            {
                captured = CapturedRequest.From(request);
                var response = new HttpResponseMessage(
                    HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("sample")),
                    RequestMessage = request
                };
                response.Headers.TryAddWithoutValidation(
                    "CF-Cache-Status",
                    "HIT");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 5, 5000);
                return response;
            }));

        var result = await service.ProbeAsync(
            Profile(),
            null,
            new Uri("https://cdn.example/file.bin"),
            1024,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(6, result.BytesRead);
        Assert.Equal(5000, result.ContentLength);
        Assert.Contains("HIT", result.CacheStatus, StringComparison.Ordinal);
        Assert.Equal("bytes=0-1023", captured?.Range);
    }

    [Fact]
    public async Task DownloadStreamsWholeResponseAndAppliesCredential()
    {
        CapturedRequest? captured = null;
        var payload = Encoding.UTF8.GetBytes("payload from CDN");
        var service = new GenericHttpCdnDeliveryService(
            _ => new StubHandler(request =>
            {
                captured = CapturedRequest.From(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload),
                    RequestMessage = request
                };
            }));
        var credential = new CdnCredential
        {
            Name = "download token",
            AuthenticationType = CdnAuthenticationType.BearerToken,
            Secret = "secret"
        };
        await using var destination = new MemoryStream();

        var result = await service.DownloadAsync(
            Profile(),
            credential,
            new Uri("https://cdn.example/assets/file.txt"),
            destination,
            CancellationToken.None);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(payload.Length, result.BytesWritten);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(HttpMethod.Get, captured?.Method);
        Assert.Null(captured?.Range);
        Assert.Equal("Bearer secret", captured?.Authorization);
    }

    [Fact]
    public async Task DownloadRejectsErrorResponseWithoutWritingBody()
    {
        var service = new GenericHttpCdnDeliveryService(
            _ => new StubHandler(request => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found"),
                RequestMessage = request
            }));
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.DownloadAsync(
                Profile(),
                null,
                new Uri("https://cdn.example/missing.txt"),
                destination,
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task HeadProbesCanObserveMissThenHitWithoutDownloadingContent()
    {
        var attempts = 0;
        var methods = new List<HttpMethod>();
        var service = new GenericHttpCdnDeliveryService(
            _ => new StubHandler(request =>
            {
                methods.Add(request.Method);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[4096]),
                    RequestMessage = request
                };
                response.Headers.TryAddWithoutValidation("X-Cache", ++attempts == 1 ? "MISS" : "HIT");
                response.Content.Headers.ContentLength = 4096;
                return response;
            }));
        var profile = Profile();
        var url = new Uri("https://cdn.example/file.bin");

        var first = await service.ProbeHeadAsync(profile, null, url, CancellationToken.None);
        var second = await service.ProbeHeadAsync(profile, null, url, CancellationToken.None);

        Assert.All(methods, method => Assert.Equal(HttpMethod.Head, method));
        Assert.Equal(0, first.BytesRead);
        Assert.Equal(0, second.BytesRead);
        Assert.Contains("MISS", first.CacheStatus, StringComparison.Ordinal);
        Assert.Contains("HIT", second.CacheStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificateInspectorReadsValidityFromRealTlsHandshake()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        using var certificate = CreateServerCertificate(
            checkedAt.AddDays(-1),
            checkedAt.AddDays(45));
        var result = await InspectLoopbackCertificateAsync(certificate, checkedAt);

        Assert.Equal("localhost", result.Endpoint.Host);
        Assert.InRange(result.DaysRemaining, 44, 45);
        Assert.False(result.Problems.HasFlag(CdnCertificateProblems.Expired));
        Assert.False(result.Problems.HasFlag(CdnCertificateProblems.NotYetValid));
        Assert.False(result.Problems.HasFlag(CdnCertificateProblems.NameMismatch));
        Assert.True(result.Problems.HasFlag(CdnCertificateProblems.UntrustedChain));
        Assert.Contains("CN=localhost", result.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, result.Sha256Fingerprint.Length);
        Assert.NotEmpty(result.TlsProtocol);
    }

    [Fact]
    public async Task CertificateInspectorReportsExpiredCertificate()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        using var certificate = CreateServerCertificate(
            checkedAt.AddDays(-10),
            checkedAt.AddDays(-2));
        var result = await InspectLoopbackCertificateAsync(certificate, checkedAt);

        Assert.True(result.Problems.HasFlag(CdnCertificateProblems.Expired));
        Assert.Contains("已过期", result.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificateInspectorReportsNameMismatch()
    {
        var checkedAt = DateTimeOffset.UtcNow;
        using var certificate = CreateServerCertificate(
            checkedAt.AddDays(-1),
            checkedAt.AddDays(45));
        var result = await InspectLoopbackCertificateAsync(
            certificate,
            checkedAt,
            host: "127.0.0.1");

        Assert.True(result.Problems.HasFlag(CdnCertificateProblems.NameMismatch));
        Assert.Contains("域名不匹配", result.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificateInspectorRejectsNonHttpsProfileWithoutConnecting()
    {
        var profile = Profile() with { BaseUrl = "http://cdn.example" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new TlsCdnCertificateInspector().InspectAsync(profile, CancellationToken.None));

        Assert.Contains("https://", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeUsesRealLoopbackHttpTransport()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = client.GetStream();
            var requestHeaders = await ReadHttpHeadersAsync(stream, timeout.Token);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 206 Partial Content\r\n" +
                "Content-Length: 6\r\n" +
                "Content-Range: bytes 0-5/4096\r\n" +
                "Content-Type: application/octet-stream\r\n" +
                "X-Cache: HIT\r\n" +
                "Connection: close\r\n\r\n" +
                "sample");
            await stream.WriteAsync(response, timeout.Token);
            return requestHeaders;
        }, timeout.Token);
        var url = new Uri($"http://127.0.0.1:{port}/assets/file.bin");

        var result = await new GenericHttpCdnDeliveryService().ProbeAsync(
            Profile() with { BaseUrl = $"http://127.0.0.1:{port}" },
            null,
            url,
            1024,
            timeout.Token);
        var request = await server.WaitAsync(timeout.Token);

        Assert.True(result.Success);
        Assert.Equal(6, result.BytesRead);
        Assert.Equal(4096, result.ContentLength);
        Assert.Contains("HIT", result.CacheStatus, StringComparison.Ordinal);
        Assert.Contains("Range: bytes=0-1023", request, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WarmupCanUseFullGet()
    {
        CapturedRequest? captured = null;
        var service = new GenericHttpCdnDeliveryService(
            _ => new StubHandler(request =>
            {
                captured = CapturedRequest.From(request);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[2048]),
                    RequestMessage = request
                };
            }));
        var profile = Profile() with
        {
            WarmupMode = CdnWarmupMode.FullGet
        };

        var result = await service.WarmupAsync(
            profile,
            null,
            new Uri("https://cdn.example/file.bin"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2048, result.BytesRead);
        Assert.Equal(HttpMethod.Get, captured?.Method);
        Assert.Null(captured?.Range);
    }

    [Fact]
    public async Task PurgeExpandsTemplatesAndAppliesBearerCredential()
    {
        CapturedRequest? captured = null;
        var service = new GenericHttpCdnDeliveryService(
            _ => new StubHandler(request =>
            {
                captured = CapturedRequest.From(request);
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("queued"),
                    RequestMessage = request
                };
            }));
        var profile = Profile() with
        {
            PurgeEndpointTemplate =
                "https://api.example/purge?target={url}",
            PurgeBodyTemplate = "{\"path\":\"{path}\"}"
        };
        var credential = new CdnCredential
        {
            Name = "token",
            AuthenticationType = CdnAuthenticationType.BearerToken,
            Secret = "secret"
        };

        var result = await service.PurgeAsync(
            profile,
            credential,
            new Uri("https://cdn.example/assets/a b.js"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(
            "target=https%3A%2F%2Fcdn.example%2Fassets%2Fa%2520b.js",
            captured?.Uri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.Equal("Bearer secret", captured?.Authorization);
        Assert.Contains(
            "/assets/a%20b.js",
            captured?.Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PurgeReadsOnlyBoundedResponseSnippet()
    {
        var stream = new CountingStream(1024 * 1024);
        var service = new GenericHttpCdnDeliveryService(
            _ => new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
                RequestMessage = request
            }));
        var profile = Profile() with
        {
            PurgeEndpointTemplate = "https://api.example/purge?target={url}"
        };

        var result = await service.PurgeAsync(
            profile,
            null,
            new Uri("https://cdn.example/file.bin"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(4096, result.ResponseSnippet.Length);
        Assert.InRange(stream.BytesRead, 4096, 16 * 1024);
    }

    private static CdnProfile Profile() => new()
    {
        Name = "site",
        BaseUrl = "https://cdn.example"
    };

    private static X509Certificate2 CreateServerCertificate(
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        using var generated = request.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            password: null);
    }

    private static async Task<CdnCertificateCheckResult> InspectLoopbackCertificateAsync(
        X509Certificate2 certificate,
        DateTimeOffset checkedAt,
        string host = "localhost")
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.None
                },
                timeout.Token);
        }, timeout.Token);

        var profile = Profile() with { BaseUrl = $"https://{host}:{port}" };
        var result = await new TlsCdnCertificateInspector(() => checkedAt)
            .InspectAsync(profile, timeout.Token);
        await server.WaitAsync(timeout.Token);
        return result;
    }

    private static string TemporaryFile(string name)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "s3explorer-cdn-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    private static void DeleteDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) &&
            Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private static async Task<string> ReadHttpHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var lastFour = new Queue<byte>(4);
        var single = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(single, cancellationToken);
            if (read == 0) break;
            buffer.WriteByte(single[0]);
            lastFour.Enqueue(single[0]);
            if (lastFour.Count > 4) lastFour.Dequeue();
            if (lastFour.SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                return Encoding.ASCII.GetString(buffer.ToArray());
        }
        throw new InvalidDataException("Loopback HTTP request headers were incomplete.");
    }

    private sealed class TestProtector : ICredentialProtector
    {
        public string Protect(string plaintext) =>
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes("protected:" + plaintext));

        public string Unprotect(string ciphertext)
        {
            var value = Encoding.UTF8.GetString(
                Convert.FromBase64String(ciphertext));
            return value["protected:".Length..];
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class CountingStream(long length) : Stream
    {
        private long _position;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - _position);
            if (read <= 0) return 0;
            Array.Fill(buffer, (byte)'x', offset, read);
            _position += read;
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - _position);
            if (read <= 0) return ValueTask.FromResult(0);
            buffer.Span[..read].Fill((byte)'x');
            _position += read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Range,
        string? Authorization,
        string Body)
    {
        public static CapturedRequest From(HttpRequestMessage request) => new(
            request.Method,
            request.RequestUri!,
            request.Headers.Range?.ToString(),
            request.Headers.Authorization?.ToString(),
            request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()
                ?? string.Empty);
    }
}
