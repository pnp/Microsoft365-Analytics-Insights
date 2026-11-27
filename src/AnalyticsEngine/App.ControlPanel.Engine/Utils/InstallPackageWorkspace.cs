using App.ControlPanel.Engine.Entities;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Owns one installation's release files until the last stage that consumes them has completed.
    /// </summary>
    public sealed class InstallPackageWorkspace : IDisposable
    {
        private readonly ILogger _logger;
        private int _nextDirectory;
        private bool _disposed;
        internal string RootPath { get; }

        public InstallPackageWorkspace(ILogger logger)
            : this(logger, StringUtils.TempDirPath)
        {
        }

        internal InstallPackageWorkspace(ILogger logger, string parentDirectory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            RootPath = Directory.CreateDirectory(Path.Combine(parentDirectory, Guid.NewGuid().ToString("N"))).FullName;
        }

        // Only one run writes under RootPath. Short child names leave room for deeply nested release assets on Windows.
        internal string CreateDirectory()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(InstallPackageWorkspace));
            return Directory.CreateDirectory(Path.Combine(RootPath,
                Interlocked.Increment(ref _nextDirectory).ToString(CultureInfo.InvariantCulture))).FullName;
        }

        internal async Task<LocalStorageInstallSourceInfo> StageLocalPackagesAsync(LocalStorageInstallSourceInfo inputs, CancellationToken cancellationToken)
        {
            var staged = new LocalStorageInstallSourceInfo();
            foreach (var component in inputs.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Different overrides can have the same filename. Do not inherit the operator's read-only attribute.
                var source = component.Value.FileLocation;
                var copy = Path.Combine(CreateDirectory(), Path.GetFileName(source));
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, 81920, cancellationToken);
                }
                staged.GetSolutionComponentLocation(component.Key).FileLocation = copy;
            }
            return staged;
        }

        internal DirectoryInfo Unzip(LocalStorageBlobInfo source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extractPath = CreateDirectory();
            var root = extractPath + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(source.FileLocation))
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The release ZIP contains a path outside its extraction directory.");
                    if (entry.Name.Length == 0)
                        Directory.CreateDirectory(destination);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination);
                    }
                }
            }

            return FindContentsRoot(new DirectoryInfo(extractPath));
        }

        private static DirectoryInfo FindContentsRoot(DirectoryInfo extractPathDir)
        {
            // Zip files are sometimes 1 dir too deep.
            if (extractPathDir.GetFiles().Length == 0 && extractPathDir.GetDirectories().Length == 1)
            {
                return FindContentsRoot(extractPathDir.GetDirectories()[0]);
            }

            return extractPathDir;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Do not mask a deployment failure or cancellation if another process still has a file open.
                _logger.LogWarning("Could not remove the installer workspace {Path}. Close any open files and remove this directory manually.", RootPath);
            }
        }
    }
}
