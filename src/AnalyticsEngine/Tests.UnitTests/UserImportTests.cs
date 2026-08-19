using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for user import and user-related functionality
    /// </summary>
    [TestClass]
    public class UserImportTests
    {
        // Removing test as devops environment has too many users and test times out
        //[TestMethod]
        public async Task UserMetadataUpdaterTests()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                // Update users

                var authConfig = new AppConfig();
                var auth = new GraphAppIndentityOAuthContext(AnalyticsLogger.ConsoleOnlyTracer(), authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);
                await auth.InitClientCredential();
                var graphClient = new GraphServiceClient(auth.Creds);

                // Get Allan user from Graph & insert blanks into DB (needs license)
                var graphUsers = await graphClient.Users.GetAsync(rc =>
                {
                    rc.QueryParameters.Filter = "startswith(mail,'AllanD')";
                    rc.QueryParameters.Top = 1;
                });
                var graphUser = graphUsers.Value[0];

                // Run updater; force full load
                var logger = AnalyticsLogger.ConsoleOnlyTracer();
                var userUpdater = new UserMetadataUpdater(logger, authConfig, auth.Creds, new ManualGraphCallClient(auth, logger));
                await userUpdater.UserLoader.DeltaValueProvider.ClearDeltaToken();

                await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();

                // Check our user just updated. Should be updated now with actual data
                var dbTestUser = await db.users
                    .Include(u => u.OfficeLocation)
                    .Include(u => u.UsageLocation)
                    .Include(u => u.LicenseLookups.Select(l => l.License))
                    .Include(u => u.JobTitle)
                    .Include(u => u.Department)
                    .Where(u => u.UserPrincipalName == graphUser.Mail).SingleOrDefaultAsync();

                Assert.IsTrue(dbTestUser.LicenseLookups.Count > 0);
                Assert.IsNotNull(dbTestUser.OfficeLocation);
                Assert.IsNotNull(dbTestUser.UsageLocation);
                Assert.IsNotNull(dbTestUser.Department);
                Assert.IsTrue(dbTestUser.AccountEnabled);

                // Update again. Should use the delta this time
                await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();


                // Update again with no delta. Test logic for updating just existing
                await userUpdater.UserLoader.DeltaValueProvider.ClearDeltaToken();
                await userUpdater.InsertAndUpdateDatabaseFromExternalUsers();
            }
        }

        [TestMethod]
        public void OfficeLicenseNameResolverTest()
        {
            var resolver = new OfficeLicenseNameResolver();
            Assert.IsTrue(resolver.GetDisplayNameFor("DYN365_BUSCENTRAL_ESSENTIAL") == "Dynamics 365 Business Central Essentials");

            Assert.IsNull(resolver.GetDisplayNameFor(""));
        }

    }
}
