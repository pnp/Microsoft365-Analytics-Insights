using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// How the installer downloads a release's packages, against a fake GitHub (no network). On a slow link the five
    /// parallel downloads used to hit HttpClient's default 100-second timeout, and the run ended as
    /// "FATAL: Unexpected error of type 'TaskCanceledException'", naming neither the package nor the timeout (#852 R11-11).
    /// Each package is also checked against the SHA-256 the release build published in release-manifest.json before
    /// anything runs it (#852 R11-14).
    /// </summary>
    [TestClass]
    public class ReleasePackageDownloadTests
    {
        const string Owner = "contoso", Repo = "analytics";

        static readonly string[] Packages =
        {
            "Office365ActivityImporter.zip", "AppInsightsImporter.zip", "AITrackerInstaller.zip", "ControlPanelApp.zip", "Website.zip"
        };

        string _directory;

        [TestInitialize]
        public void CreateDirectory() => _directory = Path.Combine(Path.GetTempPath(), "contoso-release-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void DeleteDirectory()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task EveryPackage_IsWrittenToDiskWhole()
        {
            var github = new FakeGitHub();

            var result = await NewTask(github, new CapturingLogger()).ExecuteTaskReturnResult(null);

            Assert.IsTrue(result.IsValid);
            foreach (var package in Packages)
            {
                CollectionAssert.AreEqual(FakeGitHub.Content(package), File.ReadAllBytes(Path.Combine(_directory, package)), package);
            }
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task APackageThatNeverAnswers_TimesOut_NamingOnlyThatPackage()
        {
            var github = new FakeGitHub();
            github.Behaviour["ControlPanelApp.zip"] = FakeGitHub.Hang;
            var task = NewTask(github, new CapturingLogger());
            task.StallTimeout = TimeSpan.FromSeconds(1);

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => task.ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "Could not download 1 of 5 packages of GitHub release '1859'");
            StringAssert.Contains(ex.Message, "'ControlPanelApp.zip' timed out: no data arrived for 1 second");
            foreach (var package in Packages.Where(p => p != "ControlPanelApp.zip"))
            {
                Assert.IsFalse(ex.Message.Contains(package), package + " downloaded, so it must not be blamed.");
            }
            Assert.IsFalse(File.Exists(Path.Combine(_directory, "ControlPanelApp.zip")));
        }

        /// <summary>
        /// The first chunk is taken before the rest arrives, which buffering the whole package in memory would never
        /// do; then the connection goes quiet and the partial file is removed.
        /// </summary>
        [TestMethod]
        [Timeout(30000)]
        public async Task APackageThatStallsPartWay_IsStreamed_ThenTimesOut_LeavingNoPartialFile()
        {
            var github = new FakeGitHub();
            github.Behaviour["Website.zip"] = ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowStream(firstChunk: 100 * 1024, thenEvery: Timeout.InfiniteTimeSpan))
            });
            var task = NewTask(github, new CapturingLogger());
            task.StallTimeout = TimeSpan.FromSeconds(1);

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => task.ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "'Website.zip' timed out: no data arrived for 1 second (0.1 MB received)");
            Assert.IsFalse(File.Exists(Path.Combine(_directory, "Website.zip")), "A truncated package must not be left behind.");
        }

        /// <summary>A download that keeps moving is not a stall, but it still has an overall limit.</summary>
        [TestMethod]
        [Timeout(30000)]
        public async Task APackageThatTrickles_TimesOutOnTheOverallLimit()
        {
            var github = new FakeGitHub();
            github.Behaviour["Office365ActivityImporter.zip"] = ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowStream(firstChunk: 1, thenEvery: TimeSpan.FromMilliseconds(20)))
            });
            var task = NewTask(github, new CapturingLogger());
            task.DownloadTimeout = TimeSpan.FromSeconds(1);

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => task.ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "'Office365ActivityImporter.zip' timed out: it did not finish within 1 second");
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task AMissingPackage_NamesThePackageAndWhatGitHubSaid()
        {
            var github = new FakeGitHub();
            github.Behaviour["AppInsightsImporter.zip"] = ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() =>
                NewTask(github, new CapturingLogger()).ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "'AppInsightsImporter.zip' failed: GitHub answered 404 (Not Found)");
            Assert.IsFalse(ex.Message.Contains("timed out"));
        }

        /// <summary>
        /// Pressing Cancel is the user's cancel, not a failure or a timeout: it surfaces as an OperationCanceledException,
        /// which the install reports as cancelled, and the log says which packages were still downloading or waiting to.
        /// </summary>
        [TestMethod]
        [Timeout(30000)]
        public async Task CancelWhileDownloading_IsReportedAsTheUsersCancel()
        {
            var github = new FakeGitHub();
            foreach (var package in Packages) github.Behaviour[package] = FakeGitHub.Hang;
            var logger = new CapturingLogger();

            using (var cancellation = new CancellationTokenSource())
            {
                var task = NewTask(github, logger);
                task.CancellationToken = cancellation.Token;

                var run = task.ExecuteTaskReturnResult(null);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (github.PackageRequests < LatestStableSoftwarePackageDownloadTask.ParallelDownloads && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(20);
                }
                Assert.AreEqual(LatestStableSoftwarePackageDownloadTask.ParallelDownloads, github.PackageRequests, "The downloads did not start.");
                cancellation.Cancel();

                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => run);
            }

            var cancelled = logger.Lines.Single(l => l.Message.StartsWith("Release download cancelled by the user before")).Message;
            foreach (var package in Packages)
            {
                StringAssert.Contains(cancelled, package);
            }
            Assert.IsFalse(logger.Lines.Any(l => l.Message.Contains("timed out") || l.Level >= LogLevel.Warning));
        }

        /// <summary>
        /// .NET Framework opens two connections to a server, so the other packages wait for one. Their clocks must not run
        /// while they wait: on a slow link a package queued behind two large ones would otherwise "stall" before it started.
        /// </summary>
        [TestMethod]
        [Timeout(60000)]
        public async Task PackagesWaitingForAConnection_AreNotTimedOutForTheWait()
        {
            var github = new FakeGitHub();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = 0;
            var mostAtOnce = 0;
            foreach (var package in Packages)
            {
                github.Behaviour[package] = ct =>
                {
                    var now = Interlocked.Increment(ref inFlight);
                    lock (github) mostAtOnce = Math.Max(mostAtOnce, now);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new HeldStream(FakeGitHub.Content(package), release.Task, () => Interlocked.Decrement(ref inFlight)))
                    });
                };
            }
            var task = NewTask(github, new CapturingLogger());
            task.StallTimeout = TimeSpan.FromSeconds(2);

            var run = task.ExecuteTaskReturnResult(null);
            // The first two keep sending a byte at a time, so they never stall; the rest wait twice the stall limit for a slot.
            await Task.Delay(TimeSpan.FromSeconds(4));
            release.SetResult(true);
            var result = await run;

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(LatestStableSoftwarePackageDownloadTask.ParallelDownloads, mostAtOnce);
        }

        [TestMethod]
        [Timeout(30000)]
        public async Task ReleaseMetadataThatNeverArrives_TimesOut_AndSaysWhichCall()
        {
            var github = new FakeGitHub { ReleaseHangs = true };
            var task = NewTask(github, new CapturingLogger());
            task.MetadataTimeout = TimeSpan.FromSeconds(1);

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => task.ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "GitHub did not describe the latest release within 1 second");
        }

        /// <summary>
        /// The control-panel app is run on this machine with the SQL administrator password or the installer's client
        /// secret, so a package that isn't byte for byte what the release build published is refused, and deleted, before
        /// anything runs or deploys it (#852 R11-14).
        /// </summary>
        [TestMethod]
        [Timeout(30000)]
        public async Task APackageThatIsNotWhatTheReleaseBuildPublished_IsRefusedAndDeleted()
        {
            var github = new FakeGitHub();
            github.Behaviour["ControlPanelApp.zip"] = ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("Contoso: not the published package"))
            });

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() =>
                NewTask(github, new CapturingLogger()).ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "'ControlPanelApp.zip' is not the file the release build published");
            StringAssert.Contains(ex.Message, github.Manifest["ControlPanelApp.zip"]);
            Assert.IsFalse(ex.Message.Contains("'Website.zip'"), "The packages that match must not be blamed.");
            Assert.IsFalse(File.Exists(Path.Combine(_directory, "ControlPanelApp.zip")), "An unverified package must not be left where it could be run.");
        }

        [DataTestMethod]
        [Timeout(30000)]
        [DataRow("no manifest")]
        [DataRow("no entry")]
        [DataRow("not a hash")]
        public async Task AReleaseThatCantSayWhatItsPackagesShouldBe_IsRefusedBeforeAnyIsDownloaded(string gap)
        {
            var github = new FakeGitHub();
            if (gap == "no manifest") github.Manifest = null;
            else if (gap == "no entry") github.Manifest.Remove("ControlPanelApp.zip");
            else github.Manifest["ControlPanelApp.zip"] = "not-a-sha256";

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() =>
                NewTask(github, new CapturingLogger()).ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, gap == "no manifest" ? "has no release-manifest.json" : "has no SHA-256 for 'ControlPanelApp.zip'");
            Assert.AreEqual(0, github.PackageRequests, "Nothing that can't be verified should be downloaded.");
        }

        LatestStableSoftwarePackageDownloadTask NewTask(FakeGitHub github, ILogger logger)
        {
            var config = TaskConfig.GetConfigForPropAndVal(LatestStableSoftwarePackageDownloadTask.CFG_KEY_RepoOwner, Owner)
                .AddSetting(LatestStableSoftwarePackageDownloadTask.CFG_KEY_RepoName, Repo);
            return new LatestStableSoftwarePackageDownloadTask(config, logger, LatestStableSoftwarePackageDownloadTask.CreateHttpClient(github))
            {
                DownloadDirectory = _directory
            };
        }

        /// <summary>GitHub's latest-release API and its asset downloads, each package behaving as a test sets it.</summary>
        sealed class FakeGitHub : HttpMessageHandler
        {
            readonly ConcurrentDictionary<string, bool> _requested = new ConcurrentDictionary<string, bool>();

            public readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> Behaviour =
                Packages.ToDictionary(p => p, p => Ok(p));

            /// <summary>What release-manifest.json says each package's SHA-256 is: by default, what is served. Null: no manifest.</summary>
            public Dictionary<string, string> Manifest { get; set; } = Packages.ToDictionary(p => p, p => Sha256(Content(p)));

            public bool ReleaseHangs { get; set; }

            public int PackageRequests => _requested.Count;

            public static byte[] Content(string package)
            {
                // Bigger than one 80 KB read, so a package arrives in several chunks.
                var seed = Encoding.UTF8.GetBytes(package);
                return Enumerable.Range(0, 200 * 1024).Select(i => seed[i % seed.Length]).ToArray();
            }

            public static string Sha256(byte[] bytes)
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }

            public static Func<CancellationToken, Task<HttpResponseMessage>> Ok(string package) =>
                ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content(package)) });

            public static async Task<HttpResponseMessage> Hang(CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri.AbsoluteUri == $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest")
                {
                    if (ReleaseHangs) return await Hang(cancellationToken);
                    var names = Manifest == null ? Packages : Packages.Concat(new[] { "release-manifest.json" });
                    var assets = string.Join(",", names.Select(p =>
                        $"{{\"name\":\"{p}\",\"browser_download_url\":\"https://github.com/{Owner}/{Repo}/releases/download/1859/{p}\"}}"));
                    return Json($"{{\"tag_name\":\"1859\",\"assets\":[{assets}]}}");
                }

                var package = Path.GetFileName(request.RequestUri.AbsolutePath);
                if (package == "release-manifest.json")
                {
                    // The shape .github/scripts/prepare-release.mjs writes.
                    var entries = string.Join(",", Manifest.Select(m => $"{{\"name\":\"{m.Key}\",\"sha256\":\"{m.Value}\"}}"));
                    return Json($"{{\"commit\":\"0000000000000000000000000000000000000000\",\"migrations\":[],\"assets\":[{entries}]}}");
                }

                _requested[package] = true;
                return await Behaviour[package](cancellationToken);
            }

            static HttpResponseMessage Json(string json) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        /// <summary>A response body that sends <c>firstChunk</c> bytes at once, then one byte per <c>thenEvery</c>.</summary>
        sealed class SlowStream : FakeResponseBody
        {
            readonly TimeSpan _thenEvery;
            int _firstChunk;

            public SlowStream(int firstChunk, TimeSpan thenEvery)
            {
                _firstChunk = firstChunk;
                _thenEvery = thenEvery;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_firstChunk > 0)
                {
                    var sent = Math.Min(count, _firstChunk);
                    _firstChunk -= sent;
                    return sent;
                }
                await Task.Delay(_thenEvery, cancellationToken);
                buffer[offset] = 1;
                return 1;
            }
        }

        /// <summary>
        /// A response body that sends <c>content</c> a byte every tenth of a second until <c>release</c> completes, then
        /// the rest at once: never quiet long enough to stall, and finished only when the test says.
        /// </summary>
        sealed class HeldStream : FakeResponseBody
        {
            readonly byte[] _content;
            readonly Task _release;
            readonly Action _onEnd;
            int _sent;

            public HeldStream(byte[] content, Task release, Action onEnd)
            {
                _content = content;
                _release = release;
                _onEnd = onEnd;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_sent == _content.Length)
                {
                    _onEnd();
                    return 0;
                }
                if (!_release.IsCompleted)
                {
                    await Task.WhenAny(_release, Task.Delay(100, cancellationToken));
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var sending = _release.IsCompleted ? Math.Min(count, _content.Length - _sent) : 1;
                Buffer.BlockCopy(_content, _sent, buffer, offset, sending);
                _sent += sending;
                return sending;
            }
        }

        abstract class FakeResponseBody : Stream
        {
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
