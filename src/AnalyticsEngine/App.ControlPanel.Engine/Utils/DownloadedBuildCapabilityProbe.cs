using System;
using System.IO;
using System.Text;

namespace App.ControlPanel.Engine
{
    /// <summary>Whether the downloaded release supports a capability the install needs.</summary>
    public enum DownloadedBuildCapability
    {
        /// <summary>Could not be determined - the files were missing or unreadable.</summary>
        Unknown,

        /// <summary>The capability is present in the downloaded build.</summary>
        Supported,

        /// <summary>The downloaded build definitively lacks the capability.</summary>
        NotSupported,
    }

    /// <summary>
    /// Checks what the downloaded control-panel release can do, before the installer shells out to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The schema upgrade is deliberately run by the control-panel app from the release being deployed, so
    /// the database model matches the binaries that will run against it. That means the installer can be
    /// newer than the app it drives, and a capability the installer relies on may simply not exist in the
    /// downloaded build.
    /// </para>
    /// <para>
    /// Microsoft Entra ID authentication for Azure SQL is the case that matters. A build without it opens
    /// the connection with no credentials at all, and the failure surfaces as an Entity Framework error
    /// about the <c>master</c> database - which names neither Entra ID nor the real cause. There is no way
    /// to make such a build connect to an Entra-only server, so the install must stop before it starts,
    /// rather than fail confusingly part-way through.
    /// </para>
    /// <para>
    /// Detection is a byte scan for the type name in the downloaded assemblies rather than a reflection
    /// load: the assembly that carries it targets .NET Standard 2.0, and loading that into this .NET
    /// Framework process can fail to resolve its references - which would be indistinguishable from the
    /// capability being absent. A version-number threshold was rejected for the same reason it usually is:
    /// the first supporting build number is not knowable when this code is written.
    /// </para>
    /// </remarks>
    public static class DownloadedBuildCapabilityProbe
    {
        /// <summary>
        /// Type name that only exists in builds with Entra ID SQL authentication
        /// (<c>DataUtils.Sql.AzureSqlTokenAuth</c>). Stored in assembly metadata as UTF-8, so it is findable
        /// without loading the assembly.
        /// </summary>
        internal const string EntraSqlAuthMarker = "AzureSqlTokenAuth";

        /// <summary>
        /// Whether the downloaded control-panel build can authenticate to Azure SQL with Microsoft Entra ID.
        /// </summary>
        /// <param name="downloadedAppFolder">Folder the control-panel release was extracted to.</param>
        public static DownloadedBuildCapability SupportsEntraSqlAuth(DirectoryInfo downloadedAppFolder)
        {
            return HasMarker(downloadedAppFolder, EntraSqlAuthMarker);
        }

        internal static DownloadedBuildCapability HasMarker(DirectoryInfo folder, string marker)
        {
            if (folder == null || !SafeExists(folder)) return DownloadedBuildCapability.Unknown;

            FileInfo[] candidates;
            try
            {
                // Every managed file, not just the one that happens to carry the type today: which assembly
                // a type lands in is a packaging detail, and guessing it wrong would report a capable build
                // as incapable and block a valid install.
                candidates = folder.GetFiles("*.dll", SearchOption.AllDirectories);
                var exes = folder.GetFiles("*.exe", SearchOption.AllDirectories);

                var all = new FileInfo[candidates.Length + exes.Length];
                candidates.CopyTo(all, 0);
                exes.CopyTo(all, candidates.Length);
                candidates = all;
            }
            catch (Exception)
            {
                return DownloadedBuildCapability.Unknown;
            }

            var readAnything = false;
            foreach (var file in candidates)
            {
                bool readOk;
                if (FileContainsAscii(file.FullName, marker, out readOk)) return DownloadedBuildCapability.Supported;
                readAnything |= readOk;
            }

            // Only claim absence when the files were actually readable. "Nothing found because nothing could
            // be opened" must not block an install.
            return readAnything ? DownloadedBuildCapability.NotSupported : DownloadedBuildCapability.Unknown;
        }

        /// <summary>
        /// Streams a file looking for <paramref name="marker"/> as ASCII bytes. Streamed rather than read
        /// whole because the release folder holds tens of megabytes of assemblies.
        /// </summary>
        /// <param name="readOk">True when the file was opened and read without error.</param>
        internal static bool FileContainsAscii(string path, string marker, out bool readOk)
        {
            readOk = false;
            if (string.IsNullOrEmpty(marker)) return false;

            var needle = Encoding.ASCII.GetBytes(marker);

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Overlap successive blocks by (needle - 1) bytes so a match spanning a boundary is
                    // still found.
                    var overlap = needle.Length - 1;
                    var buffer = new byte[Math.Max(64 * 1024, needle.Length * 2)];
                    var carried = 0;

                    while (true)
                    {
                        var read = stream.Read(buffer, carried, buffer.Length - carried);
                        if (read <= 0) break;

                        var available = carried + read;
                        if (IndexOf(buffer, available, needle) >= 0)
                        {
                            readOk = true;
                            return true;
                        }

                        if (available >= overlap && overlap > 0)
                        {
                            Buffer.BlockCopy(buffer, available - overlap, buffer, 0, overlap);
                            carried = overlap;
                        }
                        else
                        {
                            carried = available;
                        }
                    }
                }

                readOk = true;
                return false;
            }
            catch (Exception)
            {
                // Locked, denied, vanished mid-scan: treat as "no answer from this file" rather than "absent".
                return false;
            }
        }

        static int IndexOf(byte[] haystack, int length, byte[] needle)
        {
            var last = length - needle.Length;
            for (var i = 0; i <= last; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        static bool SafeExists(DirectoryInfo folder)
        {
            try { return folder.Exists; }
            catch (Exception) { return false; }
        }
    }
}
