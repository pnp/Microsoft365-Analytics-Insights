using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using CloudInstallEngine;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
        InstallPackageWorkspace _workspace;

        [TestInitialize]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            _workspace = new InstallPackageWorkspace(new CapturingLogger(), _directory);
        }

        [TestCleanup]
        public void DeleteDirectory()
        {
            _workspace.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        [TestMethod]
        [Timeout(120000)]
        public async Task EveryPackage_IsWrittenToDiskWhole()
        {
            var github = new FakeGitHub();

            var result = await NewTask(github, new CapturingLogger()).ExecuteTaskReturnResult(null);

            Assert.IsTrue(result.IsValid);
            foreach (var component in result.Components.Values)
            {
                var package = Path.GetFileName(component.FileLocation);
                CollectionAssert.AreEqual(FakeGitHub.Content(package), File.ReadAllBytes(component.FileLocation), package);
            }
        }

        [TestMethod]
        public async Task OverlappingInstalls_KeepTheirOwnVerifiedZipsExtractedFilesAndUpload()
        {
            var first = await NewTask(ReleaseWithZips("contoso-a"), new CapturingLogger()).ExecuteTaskReturnResult(null);
            var firstControl = first.GetSolutionComponentLocation(SoftwareComponent.ControlPanel);
            var firstExtraction = _workspace.Unzip(firstControl, CancellationToken.None);
            var firstUpload = InstallAppServiceContentsTask.BuildDeploymentPackage(first, _workspace);
            var scripts = await new ProfilingScriptsLocateTask(TaskConfig.NoConfig, new CapturingLogger(),
                Azure.Core.AzureLocation.WestEurope, new Dictionary<string, string>(), _workspace).ExecuteTaskReturnResult(first);

            string secondRoot;
            using (var secondWorkspace = new InstallPackageWorkspace(new CapturingLogger(), _directory))
            {
                secondRoot = secondWorkspace.RootPath;
                var second = await NewTask(ReleaseWithZips("contoso-b"), new CapturingLogger(), secondWorkspace).ExecuteTaskReturnResult(null);
                var secondControl = second.GetSolutionComponentLocation(SoftwareComponent.ControlPanel);
                var secondExtraction = secondWorkspace.Unzip(secondControl, CancellationToken.None);
                var secondUpload = InstallAppServiceContentsTask.BuildDeploymentPackage(second, secondWorkspace);

                // Kudu opens the file by path after assembly (and reopens it if authentication falls back).
                Assert.AreEqual("contoso-b", ReadEntry(secondUpload.FullName, "release.txt"));
                Assert.AreEqual("contoso-a", ReadEntry(firstUpload.FullName, "release.txt"));
                Assert.AreEqual("contoso-a", ReadEntry(firstUpload.FullName, "app_data/jobs/continuous/Office365ActivityImporter/release.txt"));
                Assert.AreEqual("contoso-a", ReadEntry(firstUpload.FullName, "app_data/jobs/continuous/AppInsightsImporter/release.txt"));
                Assert.AreEqual("contoso-b", File.ReadAllText(Path.Combine(secondExtraction.FullName, "release.txt")));
            }
            Assert.IsFalse(Directory.Exists(secondRoot));
            Assert.AreEqual("contoso-a", ReadEntry(firstControl.FileLocation, "release.txt"));
            Assert.AreEqual("contoso-a", File.ReadAllText(Path.Combine(firstExtraction.FullName, "release.txt")));
            Assert.AreEqual("contoso-a", File.ReadAllText(scripts.WeeklyPS), "The later runbook upload opens the script by path.");
            Assert.AreEqual("contoso-a", ReadEntry(firstUpload.FullName, "release.txt"));
        }

        [TestMethod]
        public async Task FailedLocalOverrideReplayLeavesOriginalsAndEarlierRunUnchanged()
        {
            var inputs = new LocalStorageInstallSourceInfo();
            foreach (var component in new[] { SoftwareComponent.WebSite, SoftwareComponent.WebJobActivity })
            {
                // Operators need not use the release's filenames, or a different basename for each ZIP.
                var dir = Directory.CreateDirectory(Path.Combine(_directory, component.ToString()));
                var path = Path.Combine(dir.FullName, "override.zip");
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("release.txt").Open()))
                    writer.Write(component.ToString());
                inputs.GetSolutionComponentLocation(component).FileLocation = path;
            }
            var first = await _workspace.StageLocalPackagesAsync(inputs, CancellationToken.None);
            var original = inputs.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation;
            File.WriteAllText(original, "Contoso incomplete ZIP");
            string failedRoot;
            using (var secondWorkspace = new InstallPackageWorkspace(new CapturingLogger(), _directory))
            {
                failedRoot = secondWorkspace.RootPath;
                var second = await secondWorkspace.StageLocalPackagesAsync(inputs, CancellationToken.None);
                Assert.ThrowsException<InvalidDataException>(() =>
                    secondWorkspace.Unzip(second.GetSolutionComponentLocation(SoftwareComponent.WebSite), CancellationToken.None));
            }
            Assert.IsFalse(Directory.Exists(failedRoot));
            Assert.AreEqual("Contoso incomplete ZIP", File.ReadAllText(original));
            foreach (var component in inputs.Components.Keys)
                Assert.AreEqual(component.ToString(), ReadEntry(first.GetSolutionComponentLocation(component).FileLocation, "release.txt"));
        }

        private static FakeGitHub ReleaseWithZips(string tag)
        {
            var github = new FakeGitHub { Tag = tag };
            foreach (var package in Packages)
            {
                using (var bytes = new MemoryStream())
                {
                    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        using (var writer = new StreamWriter(zip.CreateEntry(package == "AppInsightsImporter.zip" ? "wrapper/release.txt" : "release.txt").Open()))
                            writer.Write(tag);
                        if (package == "Office365ActivityImporter.zip")
                        {
                            foreach (var script in new[] { "Weekly.ps1", "Aggregation_Status.ps1", "Database_Maintenance.ps1" })
                                using (var writer = new StreamWriter(zip.CreateEntry("AutomationPS/ProfilingJobs/" + script).Open()))
                                    writer.Write(tag);
                        }
                    }
                    var contents = bytes.ToArray();
                    github.Manifest[package] = FakeGitHub.Sha256(contents);
                    github.Behaviour[package] = ct => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) });
                }
            }
            return github;
        }

        private static string ReadEntry(string zipPath, string name)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            using (var reader = new StreamReader(zip.GetEntry(name).Open()))
                return reader.ReadToEnd();
        }

        [TestMethod]
        [Timeout(120000)]
        public async Task APackageThatNeverAnswers_TimesOut_NamingOnlyThatPackage()
        {
            const string stalled = "ControlPanelApp.zip";
            var github = new FakeGitHub();
            var task = NewTimedTask(github);
            github.Behaviour[stalled] = ct => github.Clocks[stalled].WaitWithoutData<HttpResponseMessage>(task.StallTimeout, ct);

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => task.ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "Could not download 1 of 5 packages of GitHub release '1859'");
            StringAssert.Contains(ex.Message, "'ControlPanelApp.zip' timed out: no data arrived for 1 second");
            Assert.AreEqual((TimeSpan?)task.StallTimeout, github.Clocks[stalled].PendingCancelledAt, "The request itself must observe cancellation at its deadline.");
            CollectionAssert.AreEquivalent(Packages, github.Clocks.Keys.ToArray(), "Every download must have its own timer.");
            foreach (var package in Packages.Where(p => p != stalled))
            {
                Assert.IsFalse(ex.Message.Contains(package), package + " downloaded, so it must not be blamed.");
                var file = Directory.GetFiles(_workspace.RootPath, package, SearchOption.AllDirectories).Single();
                CollectionAssert.AreEqual(FakeGitHub.Content(package), File.ReadAllBytes(file), package);
            }
            Assert.AreEqual(0, Directory.GetFiles(_workspace.RootPath, stalled, SearchOption.AllDirectories).Length);
        }

        /// <summary>
        /// The first chunk is taken before the rest arrives, which buffering the whole package in memory would never
        /// do; then the connection goes quiet and the partial file is removed.
        /// </summary>
        [TestMethod]
        [Timeout(120000)]
        public async Task APackageThatStallsPartWay_IsStreamed_ThenTimesOut_LeavingNoPartialFile()
        {
            const string stalled = "Website.zip";
            var github = new FakeGitHub();
            var task = NewTimedTask(github);
            var bytes = FakeGitHub.Content(stalled);
            github.Behaviour[stalled] = ct => ChunkedResponse(bytes, (sent, token) => sent < bytes.Length
                ? Task.FromResult(bytes.Length - sent)
                : github.Clocks[stalled].WaitWithoutData<int>(task.StallTimeout, token));

            var ex = await Assert.ThrowsExceptionAsync<UnexpectedInstallException>(() => task.ExecuteTaskReturnResult(null));

            StringAssert.Contains(ex.Message, "'Website.zip' timed out: no data arrived for 1 second (0.1 MB received)");
            Assert.AreEqual((TimeSpan?)task.StallTimeout, github.Clocks[stalled].PendingCancelledAt, "The pending body read must observe cancellation.");
            Assert.AreEqual(0, Directory.GetFiles(_workspace.RootPath, "Website.zip", SearchOption.AllDirectories).Length, "A truncated package must not be left behind.");
        }

        /// <summary>
        /// The installer sets no overall limit on a package download: one that keeps receiving data runs for as long as GitHub
        /// and the network allow. This one trickles for twenty times the no-data limit. At the installer's real limit of
        /// 2 minutes without data, that is a 40-minute download, which the earlier 30-minute overall limit would have failed.
        /// </summary>
        [TestMethod]
        [Timeout(120000)]
        public async Task APackageThatTrickles_FinishesHoweverLongItTakes()
        {
            const string slowPackage = "Office365ActivityImporter.zip";
            var github = new FakeGitHub();
            var task = NewTimedTask(github);
            var trickleFor = TimeSpan.FromTicks(task.StallTimeout.Ticks * 20);
            github.Behaviour[slowPackage] = ct => ChunkedResponse(FakeGitHub.Content(slowPackage), (sent, token) =>
            {
                var clock = github.Clocks[slowPackage];
                if (clock.Elapsed >= trickleFor) return Task.FromResult(int.MaxValue);
                clock.Advance(TimeSpan.FromMilliseconds(500));
                return Task.FromResult(1);
            });

            var result = await task.ExecuteTaskReturnResult(null);

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(trickleFor, github.Clocks[slowPackage].Elapsed, "The stream must outlast twenty stall intervals.");
            CollectionAssert.AreEqual(FakeGitHub.Content(slowPackage), File.ReadAllBytes(result.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).FileLocation));
        }

        [TestMethod]
        [Timeout(120000)]
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
        [Timeout(120000)]
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
        [Timeout(120000)]
        public async Task PackagesWaitingForAConnection_AreNotTimedOutForTheWait()
        {
            var github = new FakeGitHub();
            var task = NewTimedTask(github, stallSeconds: 2);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = 0;
            var inFlight = 0;
            var mostAtOnce = 0;
            foreach (var package in Packages)
            {
                github.Behaviour[package] = ct =>
                {
                    var now = Interlocked.Increment(ref inFlight);
                    lock (github) mostAtOnce = Math.Max(mostAtOnce, now);
                    var bytes = FakeGitHub.Content(package);
                    return ChunkedResponse(bytes, async (sent, token) =>
                    {
                        if (!release.Task.IsCompleted)
                        {
                            var clock = github.Clocks[package];
                            if (clock.Elapsed < TimeSpan.FromSeconds(4))
                            {
                                clock.Advance(TimeSpan.FromSeconds(1));
                                return 1;
                            }
                            if (Interlocked.Increment(ref held) == LatestStableSoftwarePackageDownloadTask.ParallelDownloads)
                                waiting.SetResult(true);
                            await release.Task;
                        }
                        if (sent == bytes.Length) Interlocked.Decrement(ref inFlight);
                        return int.MaxValue;
                    });
                };
            }
            var run = task.ExecuteTaskReturnResult(null);
            await Task.WhenAny(waiting.Task, run);
            var timersWhileQueued = github.Clocks.Count;
            release.SetResult(true);
            var result = await run;

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(LatestStableSoftwarePackageDownloadTask.ParallelDownloads, mostAtOnce);
            Assert.AreEqual(LatestStableSoftwarePackageDownloadTask.ParallelDownloads, timersWhileQueued, "Queued packages must not start their stall timers.");
        }

        [TestMethod]
        [Timeout(120000)]
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
        [Timeout(120000)]
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
            Assert.AreEqual(0, Directory.GetFiles(_workspace.RootPath, "ControlPanelApp.zip", SearchOption.AllDirectories).Length, "An unverified package must not be left where it could be run.");
        }

        [DataTestMethod]
        [Timeout(120000)]
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

        LatestStableSoftwarePackageDownloadTask NewTask(FakeGitHub github, ILogger logger, InstallPackageWorkspace workspace = null)
        {
            var config = TaskConfig.GetConfigForPropAndVal(LatestStableSoftwarePackageDownloadTask.CFG_KEY_RepoOwner, Owner)
                .AddSetting(LatestStableSoftwarePackageDownloadTask.CFG_KEY_RepoName, Repo);
            return new LatestStableSoftwarePackageDownloadTask(config, logger, workspace ?? _workspace, LatestStableSoftwarePackageDownloadTask.CreateHttpClient(github));
        }

        LatestStableSoftwarePackageDownloadTask NewTimedTask(FakeGitHub github, int stallSeconds = 1)
        {
            var task = NewTask(github, new CapturingLogger());
            task.StallTimeout = TimeSpan.FromSeconds(stallSeconds);
            task.CreateStallTimer = github.CreateStallTimer;
            return task;
        }

        /// <summary>GitHub's latest-release API and its asset downloads, each package behaving as a test sets it.</summary>
        sealed class FakeGitHub : HttpMessageHandler
        {
            readonly ConcurrentDictionary<string, bool> _requested = new ConcurrentDictionary<string, bool>();
            public readonly ConcurrentDictionary<string, DownloadClock> Clocks = new ConcurrentDictionary<string, DownloadClock>();

            public Action<TimeSpan> CreateStallTimer(string package, CancellationTokenSource source)
            {
                var clock = new DownloadClock(source);
                Assert.IsTrue(Clocks.TryAdd(package, clock), "Duplicate stall timer for " + package);
                return clock.Change;
            }

            public readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> Behaviour =
                Packages.ToDictionary(p => p, p => Ok(p));

            /// <summary>What release-manifest.json says each package's SHA-256 is: by default, what is served. Null: no manifest.</summary>
            public Dictionary<string, string> Manifest { get; set; } = Packages.ToDictionary(p => p, p => Sha256(Content(p)));

            public bool ReleaseHangs { get; set; }
            public string Tag { get; set; } = "1859";

            public int PackageRequests => _requested.Count;

            public static byte[] Content(string package)
            {
                // Bigger than one 80 KB read, so a package arrives in several chunks.
                var seed = Encoding.UTF8.GetBytes(package);
                return Enumerable.Range(0, 100 * 1024).Select(i => seed[i % seed.Length]).ToArray();
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
                        $"{{\"name\":\"{p}\",\"browser_download_url\":\"https://github.com/{Owner}/{Repo}/releases/download/{Tag}/{p}\"}}"));
                    return Json($"{{\"tag_name\":\"{Tag}\",\"assets\":[{assets}]}}");
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

        // Only this package's synthetic network waits advance its timer. Scheduling and real disk writes take no virtual time.
        sealed class DownloadClock
        {
            readonly CancellationTokenSource _source;
            TimeSpan? _deadline;

            public DownloadClock(CancellationTokenSource source) => _source = source;
            public TimeSpan Elapsed { get; private set; }
            public TimeSpan? PendingCancelledAt { get; private set; }
            public void Change(TimeSpan delay) => _deadline = delay == Timeout.InfiniteTimeSpan ? (TimeSpan?)null : Elapsed + delay;

            public void Advance(TimeSpan by)
            {
                Elapsed += by;
                if (Elapsed >= _deadline)
                {
                    _deadline = null;
                    _source.Cancel();
                }
            }

            public async Task<T> WaitWithoutData<T>(TimeSpan interval, CancellationToken token)
            {
                var pending = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() =>
                {
                    PendingCancelledAt = Elapsed;
                    pending.TrySetCanceled();
                }))
                {
                    await Task.Yield();
                    var tick = TimeSpan.FromTicks(1);
                    Advance(interval - tick);
                    Assert.IsFalse(pending.Task.IsCompleted, "The network operation was cancelled before its idle deadline.");
                    Advance(tick);
                    Assert.IsTrue(pending.Task.IsCanceled, "The idle timer did not cancel the pending network operation.");
                    return await pending.Task;
                }
            }
        }

        static Task<HttpResponseMessage> ChunkedResponse(byte[] bytes, Func<int, CancellationToken, Task<int>> nextChunkSize) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(bytes, nextChunkSize)) });

        sealed class ChunkedStream : FakeResponseBody
        {
            readonly byte[] _content;
            readonly Func<int, CancellationToken, Task<int>> _nextChunkSize;
            int _sent;

            public ChunkedStream(byte[] content, Func<int, CancellationToken, Task<int>> nextChunkSize)
            {
                _content = content;
                _nextChunkSize = nextChunkSize;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var chunk = await _nextChunkSize(_sent, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var sending = Math.Min(chunk, Math.Min(count, _content.Length - _sent));
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
