using ActivityImporter.Engine.ActivityAPI.Copilot;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace Tests.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class CopilotHistoricalCheckpointPathTests
    {
        [TestMethod]
        public void Preflight_RejectsIgnoredDirectoryInTheActualWorktree_WithoutCreatingArtifacts()
        {
            var checkpoint = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rejected-" + Guid.NewGuid().ToString("N") + ".json");
            Assert.ThrowsException<ArgumentException>(() => CopilotHistoricalRepair.ValidateCheckpointPath(checkpoint));
            AssertNoArtifacts(checkpoint);
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Preflight_RejectsGitDirectoryOrLinkedWorktreeFileInAnAncestor(bool linked)
        {
            WithPrivateDirectory(root =>
            {
                var worktree = Path.Combine(root, "worktree");
                var nested = Path.Combine(worktree, "nested", "child");
                Directory.CreateDirectory(nested);
                if (linked) File.WriteAllText(Path.Combine(worktree, ".git"), @"gitdir: ..\main\.git\worktrees\linked");
                else Directory.CreateDirectory(Path.Combine(worktree, ".git"));
                var checkpoint = Path.Combine(nested, "checkpoint.json");
                Assert.ThrowsException<ArgumentException>(() => CopilotHistoricalRepair.ValidateCheckpointPath(checkpoint));
                AssertNoArtifacts(checkpoint);
            });
        }

        [TestMethod]
        public void Preflight_AcceptsAnExistingPrivateDirectory_WithoutCreatingArtifacts()
        {
            WithPrivateDirectory(root =>
            {
                var checkpoint = Path.Combine(root, "checkpoint.json");
                Assert.AreEqual(Path.GetFullPath(checkpoint), CopilotHistoricalRepair.ValidateCheckpointPath(checkpoint));
                AssertNoArtifacts(checkpoint);
                File.WriteAllText(checkpoint, "{}");
                Assert.AreEqual(Path.GetFullPath(checkpoint), CopilotHistoricalRepair.ValidateCheckpointPath(checkpoint));
                Assert.AreEqual("{}", File.ReadAllText(checkpoint), "Preflight must not mutate an existing checkpoint.");
            });
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow(".lock")]
        [DataRow(".pending")]
        public void Preflight_RejectsDirectoryArtifactsBeforeAnyRepair(string suffix)
        {
            WithPrivateDirectory(root =>
            {
                var checkpoint = Path.Combine(root, "checkpoint.json");
                Directory.CreateDirectory(checkpoint + suffix);
                Assert.ThrowsException<ArgumentException>(() => CopilotHistoricalRepair.ValidateCheckpointPath(checkpoint));
                Assert.IsFalse(File.Exists(checkpoint));
                Assert.IsFalse(File.Exists(checkpoint + ".lock"));
                Assert.IsFalse(File.Exists(checkpoint + ".pending"));
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Preflight_RejectsImplicitOrExplicitEnvironmentWorktrees(bool explicitWorktree)
        {
            WithPrivateDirectory(root =>
            {
                var oldDirectory = Environment.GetEnvironmentVariable("GIT_DIR");
                var oldWorktree = Environment.GetEnvironmentVariable("GIT_WORK_TREE");
                try
                {
                    Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(root, "synthetic-git"));
                    Environment.SetEnvironmentVariable("GIT_WORK_TREE", explicitWorktree ? root : null);
                    var checkpoint = Path.Combine(root, "checkpoint.json");
                    Assert.ThrowsException<ArgumentException>(() => CopilotHistoricalRepair.ValidateCheckpointPath(checkpoint));
                    AssertNoArtifacts(checkpoint);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("GIT_DIR", oldDirectory);
                    Environment.SetEnvironmentVariable("GIT_WORK_TREE", oldWorktree);
                }
            });
        }

        private static void WithPrivateDirectory(Action<string> test)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "M365AnalyticsTests", "History-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); }
            finally { Directory.Delete(root, recursive: true); }
        }

        private static void AssertNoArtifacts(string checkpoint)
        {
            Assert.IsFalse(File.Exists(checkpoint));
            Assert.IsFalse(File.Exists(checkpoint + ".lock"));
            Assert.IsFalse(File.Exists(checkpoint + ".pending"));
        }
    }
}
