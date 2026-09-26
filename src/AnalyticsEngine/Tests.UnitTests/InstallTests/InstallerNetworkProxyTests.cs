using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    /// <remarks>
    /// <para>
    /// .NET 10 port. On the .NET Framework build these assert <see cref="WebRequest.DefaultWebProxy"/>, which
    /// there is what <c>HttpClient</c> and Azure.Core read, afresh for every request. On .NET 10
    /// <c>HttpClient</c> reads <see cref="HttpClient.DefaultProxy"/> instead, once, when a handler first
    /// sends - so <see cref="InstallerNetworkProxy"/> installs one switch as that default (and as
    /// <see cref="WebRequest.DefaultWebProxy"/>) and retargets it. These assert that, including the property
    /// the port exists for: a client that has already sent follows a later change
    /// (<see cref="AClientThatHasAlreadySent_FollowsALaterChange_WithoutARestart"/>).
    /// </para>
    /// <para>
    /// The Azure.Core requests use a pipeline with its own <see cref="HttpClientTransport"/>, not the
    /// process-wide shared one. In the installer the shared transport first sends after Program.Main has
    /// installed the switch, so it captures the switch; in this test process any earlier suite may already
    /// have sent through it, before the switch existed, and it would then keep the proxy it captured then.
    /// </para>
    /// </remarks>
    [TestClass]
    [DoNotParallelize]
    public class InstallerNetworkProxyTests
    {
        private static readonly InstallerProxyConfig NoProxy = new InstallerProxyConfig { UseProxy = false };

        [TestInitialize]
        public void TestInitialize()
        {
            // Every test starts from "no installer proxy": the switch installed, answering with the system proxy.
            InstallerNetworkProxy.ApplyProcessWide(NoProxy, null);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            InstallerNetworkProxy.ApplyProcessWide(NoProxy, null);
        }

        [TestMethod]
        public void TheSwitch_IsTheProcessDefaultForHttpClientAndWebRequest()
        {
            InstallerNetworkProxy.EnsureProcessWideSwitch();

            Assert.IsInstanceOfType(HttpClient.DefaultProxy, typeof(InstallerNetworkProxy.ProcessProxySwitch),
                "On .NET 10 HttpClient reads HttpClient.DefaultProxy, not WebRequest.DefaultWebProxy.");
            Assert.AreSame(HttpClient.DefaultProxy, WebRequest.DefaultWebProxy,
                "WebRequest and WebClient must follow the same switch.");
            Assert.IsNotNull(InstallerNetworkProxy.OriginalDefaultProxy);
            Assert.IsFalse(InstallerNetworkProxy.OriginalDefaultProxy is InstallerNetworkProxy.ProcessProxySwitch,
                "The switch must never answer with itself.");
        }

        [TestMethod]
        public async Task ProcessWideProxyRoutesDefaultHttpClientWebClientAndAzureCore()
        {
            using (var proxy = new FakeProxy())
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());

                await IgnoreNetworkFailure(DefaultHttpClientRequestAsync("kv-contoso-00000000"));
                Assert.IsTrue(await proxy.WaitForConnectAsync("kv-contoso-00000000"),
                    "A default new HttpClient() must use the installer's process-wide proxy.");

                await IgnoreNetworkFailure(WebClientRequestAsync());
                Assert.IsTrue(await proxy.WaitForConnectAsync("webclient-contoso-00000000"),
                    "WebClient must use the installer's process-wide proxy.");

                await IgnoreNetworkFailure(AzureCoreRequestAsync(CreateAzureCorePipeline(), "azurecore-contoso-00000000"));
                Assert.IsTrue(await proxy.WaitForConnectAsync("azurecore-contoso-00000000"),
                    "Azure.Core's HttpClientTransport must use the installer's proxy through HttpClient.DefaultProxy on .NET 10.");
            }
        }

        [TestMethod]
        public async Task WithoutApplyProcessWideDefaultHttpClientAndAzureCoreDoNotUseTheInstallerProxy()
        {
            using (var proxy = new FakeProxy())
            {
                await IgnoreNetworkFailure(DefaultHttpClientRequestAsync("kv-contoso-00000000"));
                await IgnoreNetworkFailure(AzureCoreRequestAsync(CreateAzureCorePipeline(), "azurecore-contoso-00000000"));

                Assert.AreEqual(0, proxy.Requests.Count,
                    "Negative direction: with no installer proxy, the local fake proxy saw no CONNECT requests.");
            }
        }

        /// <summary>
        /// The SQL grant's managed-identity reader (#656) sends its Azure Resource Manager GET through a plain
        /// <c>new HttpClient()</c>. On .NET 10 that reaches the installer proxy only through the switch installed as
        /// <see cref="HttpClient.DefaultProxy"/>, so this pins it to the reader itself: a later change there - its
        /// own handler, <c>UseProxy = false</c> - would otherwise send that call around the proxy (#613) unnoticed.
        /// </summary>
        [TestMethod]
        public async Task ManagedIdentityArmReader_SendsThroughTheInstallerProxy()
        {
            using (var proxy = new FakeProxy())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());

                var reader = new ArmManagedIdentityApplicationIdSource(new StaticTokenCredential());
                await IgnoreNetworkFailure(reader.GetSystemAssignedIdentityAsync(
                    "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-contoso/providers/Microsoft.Web/sites/app-contoso",
                    timeout.Token));

                Assert.IsTrue(await proxy.WaitForRequestAsync(r => r.RequestLine == "CONNECT management.azure.com:443 HTTP/1.1"),
                    "The managed-identity reader's ARM request must go through the installer's process-wide proxy.");
            }
        }

        [TestMethod]
        public async Task BasicProxyCredentialsAreSentOnlyAfterTheProxyChallenges()
        {
            using (var proxy = new FakeProxy(challengeBasic: true))
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());

                await IgnoreNetworkFailure(DefaultHttpClientRequestAsync("kv-contoso-00000000"));

                var expected = "Proxy-Authorization: Basic " +
                    Convert.ToBase64String(Encoding.ASCII.GetBytes("installer:synthetic-password"));
                Assert.IsTrue(await proxy.WaitForRequestAsync(r => r.Headers.Any(h => h.Equals(expected, StringComparison.OrdinalIgnoreCase))),
                    "The retry after a 407 challenge must carry the configured basic proxy credentials.");
            }
        }

        [TestMethod]
        public async Task AzureCorePipelineBuiltBeforeProxyChangeUsesTheCurrentDefaultProxyWhenItSends()
        {
            var pipeline = CreateAzureCorePipeline();
            using (var proxy = new FakeProxy())
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());

                await IgnoreNetworkFailure(AzureCoreRequestAsync(pipeline, "azurecore-contoso-00000000"));

                Assert.IsTrue(await proxy.WaitForConnectAsync("azurecore-contoso-00000000"),
                    "Azure.Core must not freeze the disabled proxy at pipeline creation; installer proxy changes before the request should still apply.");
            }
        }

        /// <summary>
        /// The reason the .NET 10 port is a switch rather than an assignment. A <c>SocketsHttpHandler</c> keeps
        /// the proxy object it read when it first sent, so a client in use - Azure.Core's shared transport above
        /// all - would otherwise go on using the old proxy after Proxy Configuration changed it. Through the
        /// switch, the same client and the same Azure.Core pipeline follow each change on their next request:
        /// to a proxy, to a different one, and back to none.
        /// </summary>
        [TestMethod]
        public async Task AClientThatHasAlreadySent_FollowsALaterChange_WithoutARestart()
        {
            using (var first = new FakeProxy())
            using (var second = new FakeProxy())
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            {
                var pipeline = CreateAzureCorePipeline();

                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(first.Port), new CapturingLogger());
                await IgnoreNetworkFailure(client.GetAsync("https://phase1-contoso-00000000.vault.azure.net/"));
                await IgnoreNetworkFailure(AzureCoreRequestAsync(pipeline, "phase1-azurecore-contoso-00000000"));
                Assert.IsTrue(await first.WaitForConnectAsync("phase1-contoso-00000000"), "The client must first use the first proxy.");
                Assert.IsTrue(await first.WaitForConnectAsync("phase1-azurecore-contoso-00000000"), "Azure.Core must first use the first proxy.");

                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(second.Port), new CapturingLogger());
                await IgnoreNetworkFailure(client.GetAsync("https://phase2-contoso-00000000.vault.azure.net/"));
                await IgnoreNetworkFailure(AzureCoreRequestAsync(pipeline, "phase2-azurecore-contoso-00000000"));
                Assert.IsTrue(await second.WaitForConnectAsync("phase2-contoso-00000000"),
                    "An HttpClient that has already sent must follow a later proxy change.");
                Assert.IsTrue(await second.WaitForConnectAsync("phase2-azurecore-contoso-00000000"),
                    "An Azure.Core pipeline that has already sent must follow a later proxy change.");
                Assert.IsFalse(first.Requests.Any(r => r.RequestLine.Contains("phase2-")), "Nothing may still go to the old proxy.");

                InstallerNetworkProxy.ApplyProcessWide(NoProxy, new CapturingLogger());
                await IgnoreNetworkFailure(client.GetAsync("https://phase3-contoso-00000000.vault.azure.net/"));
                await IgnoreNetworkFailure(AzureCoreRequestAsync(pipeline, "phase3-azurecore-contoso-00000000"));
                await Task.Delay(250);
                Assert.IsFalse(first.Requests.Concat(second.Requests).Any(r => r.RequestLine.Contains("phase3-")),
                    "Switched off, the same clients must stop using the installer proxy.");
            }
        }

        [TestMethod]
        public void SwitchingProxyOffRestoresTheOriginalSystemProxy()
        {
            using (var proxy = new FakeProxy())
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());
                Assert.IsInstanceOfType(InstallerNetworkProxy.CurrentProxy, typeof(WebProxy));

                InstallerNetworkProxy.ApplyProcessWide(NoProxy, new CapturingLogger());

                var original = InstallerNetworkProxy.OriginalDefaultProxy;
                Assert.AreSame(original, InstallerNetworkProxy.CurrentProxy,
                    "Switching the installer proxy off must answer with the original HttpClient.DefaultProxy object, not keep the old custom proxy.");

                var destination = new Uri("https://kv-contoso-00000000.vault.azure.net/");
                Assert.AreEqual(original.IsBypassed(destination), HttpClient.DefaultProxy.IsBypassed(destination));
                Assert.AreEqual(original.GetProxy(destination), HttpClient.DefaultProxy.GetProxy(destination));
                Assert.AreSame(CredentialCache.DefaultNetworkCredentials,
                    HttpClient.DefaultProxy.Credentials.GetCredential(destination, "Negotiate"),
                    "The restored system proxy should use default credentials so integrated-auth corporate proxies do not 407.");
            }
        }

        [TestMethod]
        public void ProxyLoggingDoesNotLeakThePassword()
        {
            using (var proxy = new FakeProxy())
            {
                var logger = new CapturingLogger();

                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), logger);

                var log = string.Join("\n", logger.Messages);
                StringAssert.Contains(log, "basic as installer");
                Assert.IsFalse(log.Contains("synthetic-password"), "The proxy password must never be logged.");
            }
        }

        /// <summary>
        /// The installer UI applies the saved preference when it opens and before every run. A preference an
        /// older build saved can fail the stricter #613 validation, and <see cref="InstallerNetworkProxy.ApplyProcessWide"/>
        /// throws for it - raised from the main form's Load event that left the installer half-initialised.
        /// The UI therefore uses the Try form, which must leave the process proxy untouched and say why.
        /// </summary>
        [DataTestMethod]
        [DataRow("proxy.contoso.com/proxy.pac", 8080, "without a path", DisplayName = "Path an older build accepted")]
        [DataRow("http://proxy.contoso.com:3128", 8080, "3128", DisplayName = "Host port contradicts the Port box")]
        [DataRow("proxy.contoso.com;", 8080, "not a valid proxy server name", DisplayName = "Host that normalises but is not a URI host")]
        [DataRow("[not-an-ipv6]", 8080, "not a valid proxy server name", DisplayName = "Malformed IPv6 literal")]
        public void TryApply_SavedPreferenceTheStricterValidationRefuses_LeavesTheProxyAloneAndSaysWhy(string host, int port, string expectedFragment)
        {
            var saved = new InstallerProxyConfig { UseProxy = true, IntegratedAuth = true, Host = host, Port = port };
            var beforeDefault = HttpClient.DefaultProxy;
            var beforeWebRequest = WebRequest.DefaultWebProxy;
            var beforeCurrent = InstallerNetworkProxy.CurrentProxy;
            var logger = new CapturingLogger();

            Assert.ThrowsException<InvalidOperationException>(() => InstallerNetworkProxy.ApplyProcessWide(saved, logger),
                "The throwing form is what the UI used to call - the reason the Try form exists.");
            AssertProxyUnchanged(beforeDefault, beforeWebRequest, beforeCurrent);

            Assert.IsFalse(InstallerNetworkProxy.TryApplyProcessWide(saved, logger, out var error));
            StringAssert.Contains(error, expectedFragment);
            AssertProxyUnchanged(beforeDefault, beforeWebRequest, beforeCurrent);
            StringAssert.Contains(string.Join("\n", logger.Messages), expectedFragment);
        }

        [TestMethod]
        public void TryApply_UsablePreference_AppliesIt()
        {
            using (var proxy = new FakeProxy())
            {
                Assert.IsTrue(InstallerNetworkProxy.TryApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger(), out var error));
                Assert.IsNull(error);

                var expected = new Uri($"http://127.0.0.1:{proxy.Port}/");
                Assert.AreEqual(expected, ((WebProxy)InstallerNetworkProxy.CurrentProxy).Address);
                Assert.AreEqual(expected, HttpClient.DefaultProxy.GetProxy(new Uri("https://kv-contoso-00000000.vault.azure.net/")),
                    "What HttpClient resolves must be the installer proxy.");
            }
        }

        private static void AssertProxyUnchanged(IWebProxy beforeDefault, IWebProxy beforeWebRequest, IWebProxy beforeCurrent)
        {
            Assert.AreSame(beforeDefault, HttpClient.DefaultProxy, "An unusable preference must not change HttpClient.DefaultProxy.");
            Assert.AreSame(beforeWebRequest, WebRequest.DefaultWebProxy, "An unusable preference must not change WebRequest.DefaultWebProxy.");
            Assert.AreSame(beforeCurrent, InstallerNetworkProxy.CurrentProxy, "An unusable preference must not retarget the process proxy.");
        }

        private static InstallerProxyConfig BasicProxyConfig(int port)
        {
            return new InstallerProxyConfig
            {
                UseProxy = true,
                Host = "127.0.0.1",
                Port = port,
                IntegratedAuth = false,
                Username = "installer",
                Password = "synthetic-password"
            };
        }

        private static async Task DefaultHttpClientRequestAsync(string host)
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            {
                await client.GetAsync($"https://{host}.vault.azure.net/");
            }
        }

        private static async Task WebClientRequestAsync()
        {
            // WebClient is obsolete on .NET 10 (SYSLIB0014) and nothing in the product uses it any more, but a
            // library still can, which is why WebRequest.DefaultWebProxy gets the same switch.
#pragma warning disable SYSLIB0014
            using (var client = new WebClient())
#pragma warning restore SYSLIB0014
            {
                await client.DownloadStringTaskAsync(new Uri("https://webclient-contoso-00000000.vault.azure.net/"));
            }
        }

        /// <summary>
        /// An Azure.Core pipeline with its own transport - see the class remarks for why not the shared one.
        /// </summary>
        private static HttpPipeline CreateAzureCorePipeline()
        {
            var options = new ClientSecretCredentialOptions { Transport = new HttpClientTransport() };
            options.Retry.MaxRetries = 0;
            return HttpPipelineBuilder.Build(options, Array.Empty<HttpPipelinePolicy>());
        }

        private static async Task AzureCoreRequestAsync(HttpPipeline pipeline, string host)
        {
            using (var message = pipeline.CreateMessage())
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                message.Request.Method = RequestMethod.Get;
                message.Request.Uri.Reset(new Uri($"https://{host}.vault.azure.net/secrets/synthetic?api-version=7.4"));
                await pipeline.SendAsync(message, cts.Token);
            }
        }

        private static async Task IgnoreNetworkFailure(Task requestTask)
        {
            try
            {
                await requestTask;
            }
            catch (Exception)
            {
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            public List<string> Messages { get; } = new List<string>();

            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Messages.Add(formatter?.Invoke(state, exception) ?? state?.ToString() ?? string.Empty);
            }
        }

        /// <summary>A token that never touches the network, so the only connection the ARM reader makes is its GET.</summary>
        private sealed class StaticTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new AccessToken("synthetic-token", DateTimeOffset.UtcNow.AddHours(1));

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private sealed class ProxyRequest
        {
            public string RequestLine { get; set; }
            public List<string> Headers { get; } = new List<string>();
        }

        private sealed class FakeProxy : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();
            private readonly List<ProxyRequest> _requests = new List<ProxyRequest>();
            private readonly bool _challengeBasic;
            private int _requestNumber;

            public FakeProxy(bool challengeBasic = false)
            {
                _challengeBasic = challengeBasic;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = Task.Run(AcceptLoopAsync);
            }

            public int Port { get; }

            public IReadOnlyList<ProxyRequest> Requests
            {
                get
                {
                    lock (_requests)
                    {
                        return _requests.ToList();
                    }
                }
            }

            public async Task<bool> WaitForConnectAsync(string host)
            {
                var expected = $"CONNECT {host}.vault.azure.net:443 HTTP/1.1";
                return await WaitForRequestAsync(r => r.RequestLine == expected);
            }

            public async Task<bool> WaitForRequestAsync(Func<ProxyRequest, bool> predicate)
            {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    lock (_requests)
                    {
                        if (_requests.Any(predicate)) return true;
                    }

                    await Task.Delay(25);
                }

                return false;
            }

            private async Task AcceptLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                        _ = Task.Run(() => HandleClientAsync(client));
                    }
                    catch (ObjectDisposedException)
                    {
                        client?.Dispose();
                        return;
                    }
                    catch (SocketException)
                    {
                        client?.Dispose();
                        return;
                    }
                }
            }

            private async Task HandleClientAsync(TcpClient client)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    client.ReceiveTimeout = 5000;
                    while (!_cts.IsCancellationRequested)
                    {
                        var request = await ReadRequestAsync(stream);
                        if (request == null) return;

                        lock (_requests)
                        {
                            _requests.Add(request);
                        }

                        var requestNumber = Interlocked.Increment(ref _requestNumber);
                        if (_challengeBasic && requestNumber == 1)
                        {
                            await WriteAsciiAsync(stream,
                                "HTTP/1.1 407 Proxy Authentication Required\r\n" +
                                "Proxy-Authenticate: Basic realm=\"contoso\"\r\n" +
                                "Content-Length: 0\r\n" +
                                "Connection: keep-alive\r\n\r\n");
                            continue;
                        }

                        await WriteAsciiAsync(stream,
                            "HTTP/1.1 502 Bad Gateway\r\n" +
                            "Content-Length: 0\r\n" +
                            "Connection: close\r\n\r\n");
                        return;
                    }
                }
            }

            private static async Task<ProxyRequest> ReadRequestAsync(NetworkStream stream)
            {
                var buffer = new byte[4096];
                var bytes = new List<byte>();
                while (bytes.Count < 16384)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read == 0) return null;
                    bytes.AddRange(buffer.Take(read));
                    var text = Encoding.ASCII.GetString(bytes.ToArray());
                    var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        var lines = text.Substring(0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
                        var request = new ProxyRequest { RequestLine = lines[0] };
                        request.Headers.AddRange(lines.Skip(1));
                        return request;
                    }
                }

                return null;
            }

            private static Task WriteAsciiAsync(NetworkStream stream, string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                return stream.WriteAsync(bytes, 0, bytes.Length);
            }

            public void Dispose()
            {
                _cts.Cancel();
                _listener.Stop();
                _cts.Dispose();
            }
        }
    }
}
