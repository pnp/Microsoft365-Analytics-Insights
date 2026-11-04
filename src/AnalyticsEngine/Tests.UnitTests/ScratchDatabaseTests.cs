using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    [TestClass]
    public class ScratchDatabaseTests
    {
        /// <summary>
        /// LocalDB creates every database with AUTO_CLOSE ON, so without the option set a fixture's database shuts down
        /// between connections - see <see cref="ScratchDatabase.CreateDatabaseSql"/>.
        /// </summary>
        [TestMethod]
        public void ScratchDatabase_IsCreatedWithAutoCloseOff()
        {
            using (var db = ScratchDatabase.Create("AutoClose"))
            {
                Assert.IsFalse((bool)db.Scalar("SELECT is_auto_close_on FROM sys.databases WHERE database_id = DB_ID();"),
                    "A fixture's database must stay open between connections, as a production database does.");
            }
        }
    }
}
