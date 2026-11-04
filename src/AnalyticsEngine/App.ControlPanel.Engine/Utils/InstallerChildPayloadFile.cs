using App.ControlPanel.Engine.Models;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Hands the downloaded control-panel app a payload that carries credentials in a file, not on its command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The installer runs the control-panel app of the release it deploys to upgrade the schema (<c>--initdb</c>) and
    /// to register the install (<c>--registerconfig</c>). Both payloads carry the SQL connection string, which holds the
    /// SQL administrator password, or the installer's client secret. A process's command line is recorded by Microsoft
    /// Defender for Endpoint, Sysmon and Security event 4688, and other processes can read it, so the command line
    /// carries only this file's name.
    /// </para>
    /// <para>
    /// The file is created with an access list that admits only the current Windows account, which the child runs as
    /// too. The child deletes an <c>--initdb</c> file as soon as it has read it, and the installer deletes either file
    /// once the child has exited.
    /// </para>
    /// <para>
    /// The installer recognises a downloaded release whose <c>--initdb</c> accepts the file by this class's name
    /// (<see cref="DownloadedBuildCapabilityProbe.ReadsInitDbPayloadFile"/>). Renaming it makes every later release
    /// look too old to receive the file.
    /// </para>
    /// </remarks>
    public static class InstallerChildPayloadFile
    {
        /// <summary>Writes <paramref name="payload"/> to a new temporary file that only the current account can open.</summary>
        /// <returns>The file's full path.</returns>
        public static string Write(string payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            var path = Path.Combine(Path.GetTempPath(), "AnalyticsInstaller-" + Guid.NewGuid().ToString("N") + ".tmp");
            var bytes = new UTF8Encoding(false).GetBytes(payload);

            // The access list is applied as the file is created, so the payload is never readable under the temp
            // folder's inherited permissions, not even briefly.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096,
                FileOptions.None, CurrentAccountOnly()))
            {
                stream.Write(bytes, 0, bytes.Length);
            }
            return path;
        }

        /// <summary>Deletes a payload file. A file that is already gone counts as deleted.</summary>
        /// <returns>False when the file is still there.</returns>
        public static bool Delete(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return !File.Exists(path);
        }

        /// <summary>
        /// Reads the value that follows <c>--initdb</c> on the command line: base64 of a payload file's path from this
        /// build's installer on, or base64 of the payload itself from earlier installers, which the child still accepts.
        /// A payload file is deleted once read.
        /// </summary>
        /// <exception cref="FormatException">The value is not base64.</exception>
        public static DatabaseUpgradeInfo ReadInitDbArgument(string argument)
        {
            var decoded = argument.Base64Decode().Trim();
            if (decoded.StartsWith("{", StringComparison.Ordinal))
            {
                return JsonConvert.DeserializeObject<DatabaseUpgradeInfo>(decoded);
            }

            var payload = File.ReadAllText(decoded);
            var info = DatabaseUpgradeInfo.GetFromBase64String(payload);
            Delete(decoded);
            return info;
        }

        static FileSecurity CurrentAccountOnly()
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.FullControl, AccessControlType.Allow));
            return security;
        }
    }
}
