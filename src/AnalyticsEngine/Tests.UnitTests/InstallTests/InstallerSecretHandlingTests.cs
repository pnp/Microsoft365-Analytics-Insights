extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models;
using App.ControlPanel;
using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Common.Entities.Installer;
using DataUtils;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Where the installer's secrets must not end up: in the configuration snapshot recorded in SQL and shown by the
    /// portal's Install log, on the downloaded installer's command line, or lost because a config file failed to open.
    /// </summary>
    /// <remarks>Every value is synthetic.</remarks>
    [TestClass]
    public class InstallerSecretHandlingTests
    {
        const string ConfigPassword = "Synthetic-Config-1";
        const string SqlPassword = "Synthetic-Sql-P@ss1";
        const string InstallerSecret = "Synthetic~Installer.Secret_1";
        const string RuntimeSecret = "Καλημέρα-Runtime-Secret";

        static SolutionInstallConfig ConfigWithSecrets()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.ResourceGroupName = "contoso-analytics";
            config.KeyVaultName = "contoso-kv";
            config.SQLServerAdminUsername = "sqladmin";
            config.SQLServerAdminPassword = SqlPassword;
            config.InstallerAccount = new AppRegistrationCredentials("00000000-0000-0000-0000-000000000001", InstallerSecret, Guid.Empty.ToString());
            config.ActivityAccount = new AppRegistrationCredentials("00000000-0000-0000-0000-000000000002", RuntimeSecret, Guid.Empty.ToString());
            config.SharePointConfig.TargetSites = new List<string> { "https://contoso.sharepoint.com/sites/Καλημέρα" };
            return config;
        }

        static List<JProperty> EncryptedSecrets(JContainer json) => json.Descendants().OfType<JProperty>()
            .Where(p => p.Name.EndsWith("Hash", StringComparison.OrdinalIgnoreCase) && ((string)p.Value)?.Length > 0)
            .ToList();

        static List<string> StringValues(JContainer json) => json.Descendants().OfType<JValue>()
            .Where(v => v.Type == JTokenType.String).Select(v => (string)v.Value).ToList();

        #region The snapshot in SQL and the Install log

        [TestMethod]
        public void InstallLog_WithholdsTheEncryptedSecretsOlderInstallersRecorded()
        {
            // What installers before this build recorded: the config file's own format, encrypted secrets included.
            var legacy = ConfigWithSecrets().ToJson(ConfigPassword);
            Assert.AreEqual(3, EncryptedSecrets(JObject.Parse(legacy)).Count, "The fixture should carry the three encrypted secrets.");

            var rows = InstallLogAPIController.PrepareForDisplay(new List<InstallLogEntryModel>
            {
                new InstallLogEntryModel { Id = 3, ConfigJson = legacy },
                new InstallLogEntryModel { Id = 2, ConfigJson = "{ \"SQLServerAdminPasswordHash\": \"c3ludGhldGlj\", " },
                new InstallLogEntryModel { Id = 1, ConfigJson = null },
            });

            var shown = JObject.Parse(rows[0].ConfigJson);
            Assert.AreEqual(0, shown.Descendants().OfType<JProperty>().Count(p => p.Name.EndsWith("Hash", StringComparison.Ordinal)),
                "Portal.Administration does not grant the SQL administrator password or the app registration secrets.");
            Assert.AreEqual("sqladmin", (string)shown["SQLServerAdminUsername"]);
            Assert.AreEqual("contoso-kv", (string)shown["KeyVaultName"]);
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", (string)shown["InstallerAccount"]["ClientId"]);
            Assert.AreEqual("https://contoso.sharepoint.com/sites/Καλημέρα", (string)shown["SharePointConfig"]["TargetSites"][0]);
            Assert.IsNull(rows[1].ConfigJson, "Text that is not JSON can't be redacted, so it must not be shown raw.");
            Assert.IsNull(rows[2].ConfigJson);
            CollectionAssert.AreEqual(new[] { true, false, false }, rows.Select(r => r.IsCurrent).ToArray());
        }

        [TestMethod]
        public void DatabaseSnapshot_CarriesNoSecret_WhileTheSavedFileKeepsThem()
        {
            var config = ConfigWithSecrets();

            var snapshot = JObject.Parse(config.ToDatabaseSnapshotJson());

            Assert.AreEqual(0, snapshot.Descendants().OfType<JProperty>().Count(p => p.Name.EndsWith("Hash", StringComparison.Ordinal)),
                "The snapshot recorded in SQL carries encrypted secrets.");
            var values = StringValues(snapshot);
            foreach (var secret in new[] { SqlPassword, InstallerSecret, RuntimeSecret })
            {
                CollectionAssert.DoesNotContain(values, secret);
            }
            Assert.AreEqual("sqladmin", (string)snapshot["SQLServerAdminUsername"]);
            Assert.AreEqual("contoso-analytics", (string)snapshot["ResourceGroupName"]);
            Assert.AreEqual(BaseSolutionInstallConfig.CurrentConfigSchemaVersion.ToString(), (string)snapshot["ConfigSchemaVersion"]);
            Assert.IsNull(snapshot["PrivateNetworking"]);

            // The saved file is where the secrets belong, and it still opens with all of them.
            var file = config.ToJson(ConfigPassword, includePrivateNetworking: true);
            Assert.AreEqual(3, EncryptedSecrets(JObject.Parse(file)).Count);
            var reopened = SolutionInstallConfig.LoadFromJson(file, ConfigPassword);
            Assert.IsTrue(reopened.CanBecomeSaveTarget);
            Assert.AreEqual(SqlPassword, reopened.Config.SQLServerAdminPassword);
            Assert.AreEqual(InstallerSecret, reopened.Config.InstallerAccount.Secret);
            Assert.AreEqual(RuntimeSecret, reopened.Config.ActivityAccount.Secret);
        }

        [TestMethod]
        public void Redactor_RemovesSecretsAtAnyDepth_AndKeepsEverythingElseAsWritten()
        {
            const string json = @"{
  ""KeyVaultName"": ""contoso-kv"",
  ""Tags"": [ { ""Name"": ""owner"", ""Value"": ""Password policy team"" } ],
  ""Applied"": ""2026-01-02T03:04:05+01:00"",
  ""Later"": {
    ""Accounts"": [ { ""ClientId"": ""00000000-0000-0000-0000-000000000000"", ""ClientSecret"": ""synthetic"", ""SecretHash"": ""c3ludGhldGlj"" } ],
    ""Storage"": ""DefaultEndpointsProtocol=https;AccountName=contoso;AccountKey=c3ludGhldGlj;EndpointSuffix=core.windows.net"",
    ""Database"": ""Server=tcp:contoso.database.windows.net;Initial Catalog=analytics;User ID=sqladmin;Password=Synthetic-P@ss1"",
    ""EntraDatabase"": ""Server=tcp:contoso.database.windows.net;Initial Catalog=analytics"",
    ""Links"": [ ""https://contoso.blob.core.windows.net/exports?sv=2024-01-01&sig=c3ludGhldGlj"", ""https://contoso.sharepoint.com/sites/Καλημέρα"" ],
    ""ApiKey"": ""synthetic"",
    ""RefreshToken"": ""synthetic"",
    ""ProxyPassword"": ""synthetic""
  }
}";
            var text = InstallConfigSnapshotRedactor.Redact(json, out var removedAny);
            var redacted = JObject.Parse(text);

            Assert.IsTrue(removedAny);
            var names = redacted.Descendants().OfType<JProperty>().Select(p => p.Name).ToList();
            foreach (var secret in new[] { "ClientSecret", "SecretHash", "Storage", "Database", "ApiKey", "RefreshToken", "ProxyPassword" })
            {
                CollectionAssert.DoesNotContain(names, secret);
            }
            CollectionAssert.AreEqual(new[] { "https://contoso.sharepoint.com/sites/Καλημέρα" },
                redacted["Later"]["Links"].Select(l => (string)l).ToArray());
            Assert.AreEqual("Server=tcp:contoso.database.windows.net;Initial Catalog=analytics", (string)redacted["Later"]["EntraDatabase"],
                "A connection string without a credential is configuration, not a secret.");
            Assert.AreEqual("00000000-0000-0000-0000-000000000000", (string)redacted["Later"]["Accounts"][0]["ClientId"]);
            Assert.AreEqual("contoso-kv", (string)redacted["KeyVaultName"]);
            Assert.AreEqual("Password policy team", (string)redacted["Tags"][0]["Value"]);
            StringAssert.Contains(text, "\"Applied\":\"2026-01-02T03:04:05+01:00\"", "Values are shown as they were recorded.");

            InstallConfigSnapshotRedactor.Redact(text, out var removedAgain);
            Assert.IsFalse(removedAgain, "What has been redacted has nothing left to remove.");
        }

        [TestMethod]
        public void StoredSnapshots_AreScrubbedOnce_AndRowsWithoutSecretsAreLeftAlone()
        {
            var legacy = ConfigWithSecrets().ToJson(ConfigPassword);
            var clean = "{\"ResourceGroupName\":\"contoso-analytics\",\"Tags\":[{\"Name\":\"owner\",\"Value\":\"Password policy team\"}]}";
            var notJson = "Password: not a configuration snapshot";

            using (var scratch = ScratchDatabase.Create("configscrub"))
            {
                // As created by the v1-0-9 migration.
                scratch.Execute("CREATE TABLE dbo.sys_configs (id int IDENTITY(1,1) PRIMARY KEY, config_json nvarchar(max) NULL, " +
                    "date_applied datetime NOT NULL, installed_by_username nvarchar(max) NULL, messages nvarchar(max) NULL);");
                foreach (var json in new[] { legacy, clean, notJson })
                {
                    scratch.Execute("INSERT dbo.sys_configs (config_json, date_applied) VALUES (N'" + json.Replace("'", "''") + "', '2026-01-01');");
                }

                int first, second;
                using (var context = new SnapshotTableContext(new SqlConnection(scratch.ConnectionString)))
                {
                    first = InstallConfigSnapshotRedactor.ScrubStoredSnapshots(context.Database);
                    second = InstallConfigSnapshotRedactor.ScrubStoredSnapshots(context.Database);
                }

                Assert.AreEqual(1, first, "Only the row with secrets in it is rewritten.");
                Assert.AreEqual(0, second, "A second run has nothing to do.");
                var scrubbed = JObject.Parse((string)scratch.Scalar("SELECT config_json FROM dbo.sys_configs WHERE id = 1"));
                Assert.AreEqual(0, scrubbed.Descendants().OfType<JProperty>().Count(p => p.Name.EndsWith("Hash", StringComparison.Ordinal)));
                Assert.AreEqual("https://contoso.sharepoint.com/sites/Καλημέρα", (string)scrubbed["SharePointConfig"]["TargetSites"][0]);
                Assert.AreEqual(clean, (string)scratch.Scalar("SELECT config_json FROM dbo.sys_configs WHERE id = 2"));
                Assert.AreEqual(notJson, (string)scratch.Scalar("SELECT config_json FROM dbo.sys_configs WHERE id = 3"));
            }
        }

        /// <summary>A context with no model and no initializer, for running the scrub against a scratch table.</summary>
        sealed class SnapshotTableContext : DbContext
        {
            static SnapshotTableContext() => Database.SetInitializer<SnapshotTableContext>(null);

            public SnapshotTableContext(DbConnection connection) : base(connection, contextOwnsConnection: true) { }
        }

        #endregion

        #region The downloaded installer's payload file

        [TestMethod]
        public void PayloadFile_OnlyTheCurrentAccountCanOpenIt()
        {
            var path = InstallerChildPayloadFile.Write("synthetic payload");
            try
            {
                var security = File.GetAccessControl(path);
                Assert.IsTrue(security.AreAccessRulesProtected, "The temp folder's permissions must not be inherited.");
                var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
                Assert.AreEqual(1, rules.Count);
                Assert.AreEqual(WindowsIdentity.GetCurrent().User, rules[0].IdentityReference);
                Assert.AreEqual(AccessControlType.Allow, rules[0].AccessControlType);
                Assert.AreEqual("synthetic payload", File.ReadAllText(path));
            }
            finally
            {
                Assert.IsTrue(InstallerChildPayloadFile.Delete(path));
            }
            Assert.IsFalse(File.Exists(path));
        }

        [TestMethod]
        public void InitDbArgument_AcceptsAPayloadFile_AndStillThePayloadItselfFromOlderInstallers()
        {
            var info = new DatabaseUpgradeInfo
            {
                ConnectionString = "Server=tcp:contoso-sql.database.windows.net;Initial Catalog=analytics;User ID=sqladmin;Password=" + SqlPassword,
                OrgURLs = new List<string> { "https://contoso.sharepoint.com/sites/Καλημέρα" },
            };

            // This build's installer names a file. The trailing space is what the app's argument joining leaves.
            var file = InstallerChildPayloadFile.Write(info.ToBase64());
            var fromFile = InstallerChildPayloadFile.ReadInitDbArgument(file.Base64Encode() + " ");
            Assert.AreEqual(info.ConnectionString, fromFile.ConnectionString);
            CollectionAssert.AreEqual(info.OrgURLs, fromFile.OrgURLs);
            Assert.IsFalse(File.Exists(file), "The app deletes the file as soon as it has read it.");

            // An older installer puts the payload itself on the command line, and a newer app must still take it.
            var inline = InstallerChildPayloadFile.ReadInitDbArgument(info.ToBase64() + " ");
            Assert.AreEqual(info.ConnectionString, inline.ConnectionString);
            CollectionAssert.AreEqual(info.OrgURLs, inline.OrgURLs);

            // A file that does not hold a payload is reported as such, not taken for a connection string, and kept.
            var notAPayload = Path.Combine(Path.GetTempPath(), "contoso-notes-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(notAPayload, "Synthetic notes, not an upgrade payload.");
            try
            {
                Assert.ThrowsException<IOException>(() => InstallerChildPayloadFile.ReadInitDbArgument(notAPayload.Base64Encode()));
                Assert.IsTrue(File.Exists(notAPayload), "Only a file that held a payload may be deleted.");
            }
            finally
            {
                File.Delete(notAPayload);
            }
        }

        [TestMethod]
        public void Probe_RecognisesThisBuildAsReadingThePayloadFile_AndAnOlderOneAsNot()
        {
            var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AnalyticsInstallerProbe-" + Guid.NewGuid().ToString("N")));
            try
            {
                File.WriteAllText(Path.Combine(folder.FullName, "Older.dll"), "synthetic assembly from an older release");
                Assert.AreEqual(DownloadedBuildCapability.NotSupported, DownloadedBuildCapabilityProbe.ReadsInitDbPayloadFile(folder));

                // The real assembly, so a rename of the class the probe looks for can't go unnoticed.
                File.Copy(typeof(InstallerChildPayloadFile).Assembly.Location, Path.Combine(folder.FullName, "App.ControlPanel.Engine.dll"));
                Assert.AreEqual(DownloadedBuildCapability.Supported, DownloadedBuildCapabilityProbe.ReadsInitDbPayloadFile(folder));
            }
            finally
            {
                folder.Delete(true);
            }
        }

        #endregion

        #region Opening a config file

        [TestMethod]
        public void DamagedConfigFile_IsReported_NotReplacedWithANewConfiguration()
        {
            foreach (var damaged in new[] { "{ \"ResourceGroupName\": ", "", "null", "{ \"SolutionConfig\": 5 }" })
            {
                var result = SolutionInstallConfig.LoadFromJson(damaged, ConfigPassword);

                Assert.IsFalse(result.LoadedOk, damaged);
                Assert.IsNull(result.Config, damaged);
                Assert.IsFalse(result.CanBecomeSaveTarget, damaged);
                Assert.IsFalse(string.IsNullOrEmpty(result.Error), damaged);
            }
        }

        /// <summary>
        /// A wrong password is caught by the cipher's padding check, which random output passes about one time in 256.
        /// Simulated here with values encrypted under the password typed (so they decrypt) next to one that isn't.
        /// </summary>
        [TestMethod]
        public void WrongPassword_KeepsNothingItDecrypted()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.ResourceGroupName = "contoso-analytics";
            config.SQLServerAdminPasswordHash = StringCipher.Encrypt(SqlPassword, "Typed-Password");
            config.InstallerAccount.SecretHash = StringCipher.Encrypt(InstallerSecret, ConfigPassword);

            var result = SolutionInstallConfig.LoadFromJson(Newtonsoft.Json.JsonConvert.SerializeObject(config), "Typed-Password");

            Assert.IsTrue(result.LoadedOk);
            Assert.IsFalse(result.DecryptedOk);
            Assert.IsFalse(result.CanBecomeSaveTarget);
            Assert.AreEqual(string.Empty, result.Config.SQLServerAdminPassword, "A value decrypted before the failure was kept.");
            Assert.AreEqual(string.Empty, result.Config.InstallerAccount.Secret);
            Assert.AreEqual("contoso-analytics", result.Config.ResourceGroupName, "The other settings still load.");
        }

        [TestMethod]
        public void SecretThatDecryptsToNoise_CountsAsAWrongPassword()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.InstallerAccount.SecretHash = StringCipher.Encrypt("\u0001\u0007\uFFFD\u001b", ConfigPassword);

            var result = SolutionInstallConfig.LoadFromJson(Newtonsoft.Json.JsonConvert.SerializeObject(config), ConfigPassword);

            Assert.IsFalse(result.DecryptedOk, "Random output that got past the padding check was accepted as a secret.");
            Assert.AreEqual(string.Empty, result.Config.InstallerAccount.Secret);
        }

        [TestMethod]
        public void OpeningWithTheWrongPassword_NeverMakesTheFileTheSaveTarget()
        {
            var path = Path.Combine(Path.GetTempPath(), "contoso-installer-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                ConfigWithSecrets().Save(path, ConfigPassword);
                var original = File.ReadAllBytes(path);

                InstallerWorkspaceTests.RunSta(() =>
                {
                    using (var form = new MainForm())
                    {
                        form.SelectedUI = form;
                        var passwords = new Queue<string>(new[] { "Not-The-Password" });

                        // Open the other settings without the secrets.
                        form.OpenConfigFile(path, passwords.Dequeue, _ => DialogResult.No, error => Assert.Fail(error));

                        Assert.IsFalse(form.HaveSavedConfig, "Save would rewrite the file without its secrets.");
                        Assert.IsNull(form.LastConfigFilePath);
                        Assert.IsNull(form.LoadedConfig.SavedConfigFileName, "The private-networking checkpoint writes to this name.");
                        Assert.AreEqual("contoso-analytics", form.LoadedConfig.ResourceGroupName);
                        Assert.AreEqual(string.Empty, form.LoadedConfig.SQLServerAdminPassword);

                        // Asked again, the right password opens it completely, and only then does Save write to it.
                        passwords.Enqueue("Not-The-Password");
                        passwords.Enqueue(ConfigPassword);
                        form.OpenConfigFile(path, passwords.Dequeue, _ => DialogResult.Yes, error => Assert.Fail(error));

                        Assert.IsTrue(form.HaveSavedConfig);
                        Assert.AreEqual(path, form.LastConfigFilePath);
                        Assert.AreEqual(SqlPassword, form.LoadedConfig.SQLServerAdminPassword);
                        Assert.AreEqual(InstallerSecret, form.LoadedConfig.InstallerAccount.Secret);
                    }
                });

                CollectionAssert.AreEqual(original, File.ReadAllBytes(path), "Opening a file must not change it.");
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// A new file's password is asked for twice: a typing mistake there isn't noticed until the file is opened again,
        /// when nothing can decrypt the secrets in it (#852 R11-7). Opening a file still asks once.
        /// </summary>
        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void ANewFilesPassword_MustBeTypedTwice_AnExistingOnesOnce(bool newFile)
        {
            InstallerWorkspaceTests.RunSta(() =>
            {
                string refused = null;
                using (var form = new EnterPasswordForm { ConfirmPassword = newFile, ShowValidationError = message => refused = message })
                {
                    form.Show();
                    var password = InstallerWorkspaceTests.Find<TextBox>(form, "txtPassword");
                    var again = InstallerWorkspaceTests.Find<TextBox>(form, "txtConfirmPassword");
                    var ok = InstallerWorkspaceTests.Find<Button>(form, "btnSave");
                    Assert.AreEqual(newFile, again.Visible);
                    Assert.IsTrue(ok.Bottom <= form.ClientSize.Height && ok.Top > InstallerWorkspaceTests.Find<GroupBox>(form, "groupBox1").Bottom,
                        "The OK button must stay below the boxes and inside the dialog.");

                    password.Text = ConfigPassword;
                    again.Text = ConfigPassword + "x";
                    ok.PerformClick();

                    if (newFile)
                    {
                        StringAssert.Contains(refused, "don't match");
                        Assert.AreNotEqual(DialogResult.OK, form.DialogResult);
                        Assert.IsNull(form.Password);

                        refused = null;
                        again.Text = ConfigPassword;
                        ok.PerformClick();
                    }

                    Assert.IsNull(refused);
                    Assert.AreEqual(DialogResult.OK, form.DialogResult);
                    Assert.AreEqual(ConfigPassword, form.Password);
                }
            });
        }

        [TestMethod]
        public void OpeningADamagedFile_KeepsWhatWasOpenAndWhereItSaves()
        {
            var good = Path.Combine(Path.GetTempPath(), "contoso-installer-" + Guid.NewGuid().ToString("N") + ".json");
            var damaged = Path.Combine(Path.GetTempPath(), "contoso-installer-" + Guid.NewGuid().ToString("N") + ".json");
            const string damagedText = "{ \"ResourceGroupName\": \"contoso-analytics\", ";
            try
            {
                ConfigWithSecrets().Save(good, ConfigPassword);
                File.WriteAllText(damaged, damagedText);

                InstallerWorkspaceTests.RunSta(() =>
                {
                    using (var form = new MainForm())
                    {
                        form.SelectedUI = form;
                        form.OpenConfigFile(good, () => ConfigPassword, _ => DialogResult.Cancel, error => Assert.Fail(error));
                        var opened = form.LoadedConfig;

                        string shownError = null;
                        form.OpenConfigFile(damaged, () => ConfigPassword, _ => DialogResult.No, error => shownError = error);

                        Assert.IsNotNull(shownError, "A damaged file must be reported.");
                        Assert.AreSame(opened, form.LoadedConfig, "A damaged file was opened as a blank configuration.");
                        Assert.IsTrue(form.HaveSavedConfig);
                        Assert.AreEqual(good, form.LastConfigFilePath);
                    }
                });

                Assert.AreEqual(damagedText, File.ReadAllText(damaged));
            }
            finally
            {
                File.Delete(good);
                File.Delete(damaged);
            }
        }

        #endregion
    }
}
