using App.ControlPanel.Engine.Entities;
using Common.DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.IO;

namespace App.ControlPanel.Engine.InstallerTasks
{
    /// <summary>
    /// Handle zip-file operations
    /// </summary>
    public class ZipFileTasks
    {
        public static DirectoryInfo Unzip(LocalStorageBlobInfo localStorageBlobInfo, ILogger logger)
        {
            var zipFile = new FileInfo(localStorageBlobInfo.FileLocation);

            var zipFileNameMinusExtension = zipFile.Name.TrimEnd(zipFile.Extension.ToCharArray());
            var extractPath = $"{StringUtils.TempDirPath}\\{zipFileNameMinusExtension}";

            // Remove previous extraction if there is one
            if (Directory.Exists(extractPath))
            {
                // Clear out dub-dirs & files
                var directory = new DirectoryInfo(extractPath);
                try
                {
                    foreach (FileInfo file in directory.GetFiles()) file.Delete();
                    foreach (DirectoryInfo subDirectory in directory.GetDirectories()) subDirectory.Delete(true);
                }
                catch (UnauthorizedAccessException ex)
                {
                    logger.LogInformation($"Warning cleaning up previous version: '{ex.Message}'. Check '{extractPath}' for locked files.");
                }

                // Delete dir
                Directory.Delete(extractPath, true);
            }
            System.IO.Compression.ZipFile.ExtractToDirectory(localStorageBlobInfo.FileLocation, extractPath);

            var extractPathDir = new DirectoryInfo(extractPath);

            // Find contents root
            var zipContentsDir = FindContentsRoot(extractPathDir);

            return zipContentsDir;
        }


        /// <summary>
        /// Recusrively find where the files are in each sub-dir
        /// </summary>
        private static DirectoryInfo FindContentsRoot(DirectoryInfo extractPathDir)
        {
            // Zip files are sometimes 1 dir too deep.
            if (extractPathDir.GetFiles().Length == 0 && extractPathDir.GetDirectories().Length == 1)
            {
                return FindContentsRoot(extractPathDir.GetDirectories()[0]);
            }

            return extractPathDir;
        }
    }
}
