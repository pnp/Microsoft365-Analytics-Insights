using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pins the in-memory lookup caches to the same idea of string equality that SQL Server has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ObjectByIdCache{T}"/> keys its dictionary with a comparer built from LCID 1033 and
    /// <c>IgnoreCase | IgnoreKanaType | IgnoreWidth</c>, chosen to mimic the database's
    /// <c>Latin1_General_CI_AS</c> collation. The two must agree: the cache decides whether a name is
    /// already known, and a unique index decides whether the insert is allowed. When they disagree the
    /// importer inserts a row SQL then rejects with a duplicate-key violation.
    /// </para>
    /// <para>
    /// They stopped agreeing on .NET 10. .NET Framework compared strings with Windows NLS; .NET 5 and
    /// later default to ICU, and ICU does not treat the German sharp s as equal to <c>ss</c> at the
    /// default strength, whereas both NLS and SQL Server do. The fix is the
    /// <c>System.Globalization.UseNls</c> switch in <c>src/AnalyticsEngine/Directory.Build.props</c>;
    /// this test is what tells you if it is ever removed - or if a workload is moved to Linux, where
    /// the runtime always uses ICU and ignores the switch.
    /// </para>
    /// <para>
    /// Deliberately has no database dependency, unlike <c>DataUtilsTests.CacheEncodingTest</c>, which
    /// covers the same ground end-to-end. When only the collation assumption is broken this fails and
    /// says so directly, instead of surfacing as a duplicate-key exception several layers away.
    /// </para>
    /// </remarks>
    [TestClass]
    public class ObjectByIdCacheCollationTests
    {
        /// <summary>A minimal cache so the comparer can be exercised without a database.</summary>
        private sealed class NameCache : ObjectByIdCache<string>
        {
            /// <summary>Nothing is ever loaded from elsewhere; the tests supply values directly.</summary>
            public override Task<string> Load(string id) => Task.FromResult<string>(null);
        }

        /// <summary>
        /// The pair that broke: identical to SQL Server, distinct to ICU, equal under NLS.
        /// </summary>
        private const string WithSs = "Gesundheits- und Krankenpfleger im Aussendienst";
        private const string WithSharpS = "Gesundheits- und Krankenpfleger im Au\u00DFendienst";

        [TestMethod]
        public async Task Cache_Treats_SharpS_And_Ss_As_The_Same_Key_Like_SQL_Server_Does()
        {
            Assert.AreNotEqual(WithSs, WithSharpS, "The two spellings must differ as ordinary .NET strings.");

            var cache = new NameCache();

            var first = await cache.GetResource(WithSs, () => Task.FromResult("first"));
            var second = await cache.GetResource(WithSharpS, () => Task.FromResult("second"));

            Assert.AreEqual("first", second,
                "The cache created a second entry for a name SQL Server considers identical, so the " +
                "importer would insert a duplicate row and take a unique-index violation. This is the " +
                "ICU/NLS difference: check that System.Globalization.UseNls is still set in " +
                "src/AnalyticsEngine/Directory.Build.props, and note it has no effect on Linux.");
            Assert.AreSame(first, second);
        }

        /// <summary>
        /// Accents must still make two names <b>different</b>, as <c>Latin1_General_CI_AS</c> is
        /// accent-sensitive.
        /// </summary>
        /// <remarks>
        /// This is the other half of the contract, and it exists to close off the obvious-looking wrong
        /// fix. Adding <c>CompareOptions.IgnoreNonSpace</c> to the comparer makes the sharp s equal to
        /// <c>ss</c> under ICU too, so it would make the test above pass without the NLS switch - but it
        /// also makes accented and unaccented names equal, which the database does not. The cache would
        /// then collapse two genuinely distinct names onto one row and silently mis-attribute data,
        /// which is far worse than the duplicate-key error it set out to cure.
        /// </remarks>
        [TestMethod]
        public async Task Cache_Keeps_Accented_Names_Distinct_Like_SQL_Server_Does()
        {
            var cache = new NameCache();

            var plain = await cache.GetResource("Cafe Contoso", () => Task.FromResult("plain"));
            var accented = await cache.GetResource("Caf\u00E9 Contoso", () => Task.FromResult("accented"));

            Assert.AreEqual("plain", plain);
            Assert.AreEqual("accented", accented,
                "The cache treated an accented name as the same as its unaccented spelling. " +
                "Latin1_General_CI_AS is accent-SENSITIVE, so these are two different rows. If the " +
                "comparer has gained CompareOptions.IgnoreNonSpace, remove it - that makes the sharp-s " +
                "test above pass for the wrong reason and merges distinct names.");
        }
    }
}
