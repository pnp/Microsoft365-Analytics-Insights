using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Tests.FakeDataGen.Demo;

namespace Tests.UnitTests
{
    /// <summary>
    /// Keeps the Container Apps demo deployment (infra/ContainerAppsDemo) in step with the generator it
    /// runs. The deployment is Bicep and PowerShell, so nothing else would notice them drifting apart.
    /// </summary>
    [TestClass]
    [TestCategory("DemoGenerator")]
    public class DemoContainerAppsTests
    {
        /// <summary>
        /// The portal decides which workloads exist from its ImportJobSettings app setting, never from
        /// the rows (see DemoPortalReadiness). The demo portal is configured by apps.bicep, so a flag the
        /// generator starts filling but the template does not switch on would ship a demo whose new
        /// panel reads "not measured" over a database full of its data.
        /// </summary>
        [TestMethod]
        public void AppsTemplate_SwitchesOnEveryWorkloadTheGeneratorFills()
        {
            var template = File.ReadAllText(ContainerAppsDemoFile("apps.bicep"));
            var match = Regex.Match(template, @"param importJobSettings string = '([^']*)'");
            Assert.IsTrue(match.Success, "apps.bicep must declare an importJobSettings parameter with a default.");
            Assert.AreEqual(DemoPortalReadiness.RequiredImportJobSettings, match.Groups[1].Value,
                "Update the importJobSettings default in infra/ContainerAppsDemo/apps.bicep to the generator's current flags.");
        }

        /// <summary>The nightly job's reset is only allowed on a ContosoDemo_ database; the deployment's default must be one.</summary>
        [TestMethod]
        public void EnvironmentExample_DefaultsToADatabaseTheGeneratorWillReset()
        {
            var example = File.ReadAllText(ContainerAppsDemoFile("demo.environment.example.json"));
            var match = Regex.Match(example, @"""databaseName""\s*:\s*""([^""]*)""");
            Assert.IsTrue(match.Success, "demo.environment.example.json must show sql.databaseName.");
            Assert.IsTrue(DemoOptions.IsValidDatabaseName(match.Groups[1].Value), match.Groups[1].Value);
        }

        private static string ContainerAppsDemoFile(string name, [CallerFilePath] string thisFilePath = "")
        {
            // thisFilePath = <repo>\src\AnalyticsEngine\Tests.UnitTests\DemoContainerAppsTests.cs
            var repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath), "..", "..", ".."));
            var path = Path.Combine(repositoryRoot, "infra", "ContainerAppsDemo", name);
            if (!File.Exists(path)) throw new FileNotFoundException("Could not find the Container Apps demo file.", path);
            return path;
        }
    }
}
