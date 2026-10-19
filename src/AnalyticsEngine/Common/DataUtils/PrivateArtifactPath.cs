using System;
using System.IO;

namespace DataUtils
{
    /// <summary>Metadata-only preflight for private files that must never enter a Git worktree.</summary>
    public static class PrivateArtifactPath
    {
        public static string Validate(string path, params string[] companionSuffixes)
        {
            var fullPath = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)
                || Path.GetFileName(fullPath).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Choose a file in an existing private directory outside Git worktrees.");
            var explicitWorktree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
            if (string.IsNullOrWhiteSpace(explicitWorktree)
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GIT_DIR")))
                throw new ArgumentException("Private artifact preflight requires an explicit GIT_WORK_TREE when GIT_DIR overrides repository discovery.");
            if (!string.IsNullOrWhiteSpace(explicitWorktree))
            {
                var root = Path.GetFullPath(explicitWorktree).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Private files must be outside every Git worktree, including ignored directories.");
            }
            ValidateArtifact(fullPath);
            foreach (var suffix in companionSuffixes)
                ValidateArtifact(fullPath + suffix);
            return fullPath;
        }

        private static void ValidateArtifact(string path)
        {
            var attributes = ExistingAttributes(path);
            if (attributes.HasValue && (attributes.Value & FileAttributes.Directory) != 0)
                throw new ArgumentException("Private artifacts must be files, not directories.");
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                attributes = ExistingAttributes(current);
                if (!attributes.HasValue) continue;
                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Private artifact paths must not traverse junctions or symbolic links.");
                if ((attributes.Value & FileAttributes.Directory) != 0
                    && ExistingAttributes(Path.Combine(current, ".git")).HasValue)
                    throw new ArgumentException("Private files must be outside every Git worktree, including ignored directories.");
            }
        }

        private static FileAttributes? ExistingAttributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }
}
