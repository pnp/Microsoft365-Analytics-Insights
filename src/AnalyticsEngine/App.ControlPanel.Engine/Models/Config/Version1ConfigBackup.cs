using App.ControlPanel.Engine.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Before this installer first overwrites a configuration file whose secrets are in <see cref="StringCipher"/>'s version 1
    /// format, keeps a copy of the file that older installers can still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This installer saves secrets in version 2, which installers from before it can't read: they say the password is wrong,
    /// and if the operator then saves, they write the file back with the secrets empty. The copy is how an operator goes back
    /// to an older installer without entering every secret again.
    /// </para>
    /// <para>
    /// Every write of a configuration file calls <see cref="BeforeOverwriting"/> first: <see cref="SolutionInstallConfig.Save"/>,
    /// which Save, Save As and the save before an installation use, and <see cref="SolutionInstallConfig.CheckpointPrivateNetworking"/>,
    /// which saves the file while an installation changes private networking.
    /// </para>
    /// <list type="bullet">
    /// <item>The copy is <c>&lt;name&gt;.v1-backup&lt;extension&gt;</c> next to the file (<c>Contoso.json</c> gets
    /// <c>Contoso.v1-backup.json</c>) and holds the bytes that were on disk, unchanged.</item>
    /// <item>It is written only when the file about to be overwritten holds a secret in version 1 format. A new file, one already
    /// in version 2 and one with no secret saved get none, so it is written once: the save it precedes leaves version 2.</item>
    /// <item>An existing copy is never replaced: it may be the only one an older installer can open.</item>
    /// <item>When the copy can't be written, the file is not overwritten either (<see cref="Version1ConfigBackupException"/>).
    /// Saving must not cost the operator the only copy an older installer can read.</item>
    /// </list>
    /// <para>
    /// The copy's secrets are protected only by version 1's weak key derivation, so the operator is told where it is and to
    /// delete it once they no longer need it (<see cref="Version1BackupResult.Notice"/>).
    /// </para>
    /// </remarks>
    internal static class Version1ConfigBackup
    {
        /// <summary>Added to the file's name, before its extension, to name the copy.</summary>
        internal const string NameSuffix = ".v1-backup";

        /// <summary>Where the copy of <paramref name="configFileName"/> goes.</summary>
        internal static string PathFor(string configFileName)
        {
            var fullPath = Path.GetFullPath(configFileName);
            return Path.Combine(Path.GetDirectoryName(fullPath),
                Path.GetFileNameWithoutExtension(fullPath) + NameSuffix + Path.GetExtension(fullPath));
        }

        /// <summary>
        /// Keeps the copy, when <paramref name="configFileName"/> needs one, before it is overwritten. Call it before every write.
        /// </summary>
        /// <exception cref="Version1ConfigBackupException">
        /// The file needs a copy and it could not be written, or the file could not be read to tell. Don't overwrite the file.
        /// </exception>
        internal static Version1BackupResult BeforeOverwriting(string configFileName)
        {
            var configPath = Path.GetFullPath(configFileName);
            var backupPath = PathFor(configPath);

            byte[] onDisk;
            try
            {
                onDisk = File.ReadAllBytes(configPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return Version1BackupResult.NotNeeded;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                throw new Version1ConfigBackupException(configPath, backupPath,
                    $"'{Path.GetFileName(configPath)}' was not saved, and is unchanged: it could not be read to check whether older " +
                    $"installers need a copy of it first. {ex.Message}", ex);
            }

            if (!HoldsVersion1Secret(onDisk)) return Version1BackupResult.NotNeeded;
            if (File.Exists(backupPath)) return Existing(configPath, backupPath, onDisk);

            FileStream copy;
            try
            {
                copy = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(backupPath))
            {
                // Written by someone else since the check above: a copy to keep, like any other.
                return Existing(configPath, backupPath, onDisk);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                throw CouldNotWrite(configPath, backupPath, ex);
            }

            try
            {
                using (copy)
                {
                    copy.Write(onDisk, 0, onDisk.Length);
                    // On the disk before the file it copies is overwritten, not just handed to the operating system.
                    copy.Flush(true);
                }
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                // A partial copy would pass for one an older installer can open. This call created it, so it may remove it.
                try { File.Delete(backupPath); }
                catch (Exception deleteFailure) when (IsFileSystemFailure(deleteFailure)) { }
                throw CouldNotWrite(configPath, backupPath, ex);
            }

            return Version1BackupResult.Written(configPath, backupPath);
        }

        /// <summary>
        /// Whether <paramref name="file"/> holds a secret in version 1 format: one an older installer can decrypt. Read from the
        /// JSON rather than through the configuration model, so a file this build can't load is still recognised.
        /// </summary>
        internal static bool HoldsVersion1Secret(byte[] file)
        {
            JObject config;
            try
            {
                using (var reader = new StreamReader(new MemoryStream(file), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    config = JObject.Parse(reader.ReadToEnd());
                }
            }
            catch (JsonException)
            {
                // Not a configuration file, or a damaged one: no installer can open it, so none loses anything.
                return false;
            }
            return SecretValues(config).Any(StringCipher.IsVersion1Format);
        }

        /// <summary>The secrets a configuration file holds, encrypted, matched by name as the installer reads them.</summary>
        private static IEnumerable<string> SecretValues(JObject config)
        {
            yield return StringValue(config, nameof(SolutionInstallConfig.SQLServerAdminPasswordHash));
            foreach (var account in new[] { nameof(SolutionInstallConfig.InstallerAccount), nameof(SolutionInstallConfig.ActivityAccount) })
            {
                yield return StringValue(config.GetValue(account, StringComparison.OrdinalIgnoreCase) as JObject,
                    nameof(AppRegistrationCredentials.SecretHash));
            }
        }

        private static string StringValue(JObject parent, string name) =>
            (parent?.GetValue(name, StringComparison.OrdinalIgnoreCase) as JValue)?.Value as string;

        /// <summary>
        /// A copy is already there, and is kept whatever it holds. When it holds this very file, kept by an earlier save that
        /// then could not write the file, the operator is told about it as a copy of the file as it was.
        /// </summary>
        private static Version1BackupResult Existing(string configPath, string backupPath, byte[] onDisk)
        {
            byte[] existing = null;
            try
            {
                existing = File.ReadAllBytes(backupPath);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                // Kept all the same; only what the operator is told depends on it.
            }
            return Version1BackupResult.AlreadyExisted(configPath, backupPath, existing != null && existing.SequenceEqual(onDisk));
        }

        private static bool IsFileSystemFailure(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is NotSupportedException || ex is System.Security.SecurityException;

        private static Version1ConfigBackupException CouldNotWrite(string configPath, string backupPath, Exception ex) =>
            new Version1ConfigBackupException(configPath, backupPath,
                $"'{Path.GetFileName(configPath)}' was not saved, and is unchanged. Saving would replace the older encryption of its " +
                "secrets with a stronger one that older installers can't read, so the installer first keeps a copy of the file that " +
                $"they can open, and that copy could not be written to '{backupPath}': {ex.Message}", ex);
    }

    /// <summary>What <see cref="Version1ConfigBackup.BeforeOverwriting"/> did.</summary>
    internal enum Version1BackupOutcome
    {
        /// <summary>There is no file, or no secret in it is in version 1 format: an older installer loses nothing.</summary>
        NotNeeded,

        /// <summary>The copy was written.</summary>
        Written,

        /// <summary>A copy already existed, and was left as it was.</summary>
        AlreadyExisted,
    }

    /// <summary>What <see cref="Version1ConfigBackup.BeforeOverwriting"/> did, and what to tell the operator about it.</summary>
    internal sealed class Version1BackupResult
    {
        internal static readonly Version1BackupResult NotNeeded = new Version1BackupResult(Version1BackupOutcome.NotNeeded, null, null);

        private Version1BackupResult(Version1BackupOutcome outcome, string backupFileName, string notice)
        {
            Outcome = outcome;
            BackupFileName = backupFileName;
            Notice = notice;
        }

        internal static Version1BackupResult Written(string configPath, string backupPath) =>
            new Version1BackupResult(Version1BackupOutcome.Written, backupPath, CopyKeptNotice(configPath, backupPath));

        /// <param name="sameAsFile">Whether the copy holds the file as it was before this save.</param>
        internal static Version1BackupResult AlreadyExisted(string configPath, string backupPath, bool sameAsFile) =>
            new Version1BackupResult(Version1BackupOutcome.AlreadyExisted, backupPath, sameAsFile
                ? CopyKeptNotice(configPath, backupPath)
                : $"'{Path.GetFileName(configPath)}' is now saved with this installer's stronger encryption for secrets, which older " +
                  $"installers can't read. A copy that older installers can open, '{backupPath}', already existed, so it was kept as " +
                  "it was rather than replaced with this version of the file. " + ProtectedOnlyByTheOlderEncryption);

        private static string CopyKeptNotice(string configPath, string backupPath) =>
            $"'{Path.GetFileName(configPath)}' is now saved with this installer's stronger encryption for secrets, which older " +
            "installers can't read: they say the password is wrong, and saving the file from one of them stores the secrets " +
            $"empty. Before saving, the installer kept a copy of the file as it was, which older installers can open: '{backupPath}'. " +
            ProtectedOnlyByTheOlderEncryption;

        private const string ProtectedOnlyByTheOlderEncryption =
            "Its secrets are protected only by the older, weaker encryption, so delete it once you no longer need to go back to an " +
            "older installer.";

        public Version1BackupOutcome Outcome { get; }

        /// <summary>The copy's full path, or null when none was needed.</summary>
        public string BackupFileName { get; }

        /// <summary>What to tell the operator once the file has been saved, or null when there is nothing to tell.</summary>
        public string Notice { get; }
    }

    /// <summary>
    /// A configuration file was not overwritten, because the copy that older installers can open could not be written first.
    /// An <see cref="IOException"/>, so a save reports it as it reports any other failure to write the file.
    /// </summary>
    internal sealed class Version1ConfigBackupException : IOException
    {
        internal Version1ConfigBackupException(string configFileName, string backupFileName, string message, Exception innerException)
            : base(message, innerException)
        {
            ConfigFileName = configFileName;
            BackupFileName = backupFileName;
        }

        public string ConfigFileName { get; }

        public string BackupFileName { get; }
    }
}
