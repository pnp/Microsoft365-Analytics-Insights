using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
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
    [TestClass]
    [DoNotParallelize]
    public class InstallerNetworkProxyTests
    {
        private IWebProxy _originalDefaultProxy;
        private ICredentials _originalWebProxyCredentials;

        [TestInitialize]
        public void TestInitialize()
        {
            _originalDefaultProxy = WebRequest.DefaultWebProxy;
            _originalWebProxyCredentials = (_originalDefaultProxy as WebProxy)?.Credentials;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            if (_originalDefaultProxy is WebProxy webProxy)
            {
                webProxy.Credentials = _originalWebProxyCredentials;
            }

            WebRequest.DefaultWebProxy = _originalDefaultProxy;
        }

        [TestMethod]
        public async Task ProcessWideProxyRoutesDefaultHttpClientWebClientAndAzureCore()
        {
            using (var proxy = new FakeProxy())
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());

                await IgnoreNetworkFailure(DefaultHttpClientRequestAsync());
                Assert.IsTrue(await proxy.WaitForRequestAsync(r => r.RequestLine == "CONNECT kv-contoso-00000000.vault.azure.net:443 HTTP/1.1"),
                    "A default new HttpClient() must use the installer's process-wide proxy.");

                await IgnoreNetworkFailure(WebClientRequestAsync());
                Assert.IsTrue(await proxy.WaitForRequestAsync(r => r.RequestLine == "CONNECT webclient-contoso-00000000.vault.azure.net:443 HTTP/1.1"),
                    "WebClient must use the installer's process-wide proxy.");

                await IgnoreNetworkFailure(AzureCoreRequestAsync());
                Assert.IsTrue(await proxy.WaitForRequestAsync(r => r.RequestLine == "CONNECT azurecore-contoso-00000000.vault.azure.net:443 HTTP/1.1"),
                    "Azure.Core's default HttpClientTransport must use WebRequest.DefaultWebProxy on .NET Framework.");
            }
        }

        [TestMethod]
        public async Task WithoutApplyProcessWideDefaultHttpClientAndAzureCoreDoNotUseTheInstallerProxy()
        {
            using (var proxy = new FakeProxy())
            {
                await IgnoreNetworkFailure(DefaultHttpClientRequestAsync());
                await IgnoreNetworkFailure(AzureCoreRequestAsync());

                Assert.AreEqual(0, proxy.Requests.Count,
                    "Negative direction: before ApplyProcessWide, the local fake proxy saw no CONNECT requests.");
            }
        }

        [TestMethod]
        public async Task BasicProxyCredentialsAreSentOnlyAfterTheProxyChallenges()
        {
            using (var proxy = new FakeProxy(challengeBasic: true))
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());

                await IgnoreNetworkFailure(DefaultHttpClientRequestAsync());

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

                await IgnoreNetworkFailure(AzureCoreRequestAsync(pipeline));

                Assert.IsTrue(await proxy.WaitForRequestAsync(r => r.RequestLine == "CONNECT azurecore-contoso-00000000.vault.azure.net:443 HTTP/1.1"),
                    "Azure.Core must not freeze the disabled proxy at pipeline creation; installer proxy changes before the request should still apply.");
            }
        }

        [TestMethod]
        public void SwitchingProxyOffRestoresTheOriginalSystemProxy()
        {
            using (var proxy = new FakeProxy())
            {
                InstallerNetworkProxy.ApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger());
                Assert.IsInstanceOfType(WebRequest.DefaultWebProxy, typeof(WebProxy));

                InstallerNetworkProxy.ApplyProcessWide(new InstallerProxyConfig { UseProxy = false }, new CapturingLogger());

                Assert.AreSame(_originalDefaultProxy, WebRequest.DefaultWebProxy,
                    "Switching the installer proxy off must restore the system proxy object, not keep the old custom proxy.");
                if (WebRequest.DefaultWebProxy is WebProxy restoredProxy)
                {
                    Assert.AreSame(CredentialCache.DefaultCredentials, restoredProxy.Credentials,
                        "The restored system proxy should use default credentials so integrated-auth corporate proxies do not 407.");
                }
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
        public void TryApply_SavedPreferenceTheStricterValidationRefuses_LeavesTheProxyAloneAndSaysWhy(string host, int port, string expectedFragment)
        {
            var saved = new InstallerProxyConfig { UseProxy = true, IntegratedAuth = true, Host = host, Port = port };
            var before = WebRequest.DefaultWebProxy;
            var logger = new CapturingLogger();

            Assert.ThrowsException<InvalidOperationException>(() => InstallerNetworkProxy.ApplyProcessWide(saved, logger),
                "The throwing form is what the UI used to call - the reason the Try form exists.");
            Assert.AreSame(before, WebRequest.DefaultWebProxy);

            Assert.IsFalse(InstallerNetworkProxy.TryApplyProcessWide(saved, logger, out var error));
            StringAssert.Contains(error, expectedFragment);
            Assert.AreSame(before, WebRequest.DefaultWebProxy, "An unusable preference must not change the process proxy.");
            StringAssert.Contains(string.Join("\n", logger.Messages), expectedFragment);
        }

        [TestMethod]
        public void TryApply_UsablePreference_AppliesIt()
        {
            using (var proxy = new FakeProxy())
            {
                Assert.IsTrue(InstallerNetworkProxy.TryApplyProcessWide(BasicProxyConfig(proxy.Port), new CapturingLogger(), out var error));
                Assert.IsNull(error);
                Assert.AreEqual(new Uri($"http://127.0.0.1:{proxy.Port}/"), ((WebProxy)WebRequest.DefaultWebProxy).Address);
            }
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

        private static async Task DefaultHttpClientRequestAsync()
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            {
                await client.GetAsync("https://kv-contoso-00000000.vault.azure.net/");
            }
        }

        private static async Task WebClientRequestAsync()
        {
            using (var client = new WebClient())
            {
                await client.DownloadStringTaskAsync(new Uri("https://webclient-contoso-00000000.vault.azure.net/"));
            }
        }

        private static async Task AzureCoreRequestAsync()
        {
            await AzureCoreRequestAsync(CreateAzureCorePipeline());
        }

        private static HttpPipeline CreateAzureCorePipeline()
        {
            var options = new ClientSecretCredentialOptions();
            options.Retry.MaxRetries = 0;
            return HttpPipelineBuilder.Build(options, Array.Empty<HttpPipelinePolicy>());
        }

        private static async Task AzureCoreRequestAsync(HttpPipeline pipeline)
        {
            using (var message = pipeline.CreateMessage())
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                message.Request.Method = RequestMethod.Get;
                message.Request.Uri.Reset(new Uri("https://azurecore-contoso-00000000.vault.azure.net/secrets/synthetic?api-version=7.4"));
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
