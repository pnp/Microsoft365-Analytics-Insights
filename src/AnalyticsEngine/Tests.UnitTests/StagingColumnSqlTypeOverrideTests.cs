using DataUtils.Sql;
using DataUtils.Sql.Inserts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using WebJob.AppInsightsImporter.Engine.Sql.Models;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for <see cref="ColumnAttribute.SqlTypeOverride"/> -> <see cref="ColumnSqlInfo.SqlColDefinition"/>
    /// plumbing in <see cref="InsertBatchTypeFieldCache{T}"/>, plus regression tests that the two
    /// production staging join columns (<c>##import_staging_hit_imports.url</c> and
    /// <c>##import_staging_event_lookups.object_id</c>) really do declare
    /// <c>varchar(1700)</c> so that the post-PR-#108 <c>IX_urls_full_url</c> index is
    /// usable by the staging-table merges. See issue #109.
    /// </summary>
    [TestClass]
    public class StagingColumnSqlTypeOverrideTests
    {
        private const string ExpectedUrlSqlType = "varchar(1700)";
        private const string DefaultStringSqlType = "[nvarchar] (max)";

        /// <summary>
        /// A property without <see cref="ColumnAttribute.SqlTypeOverride"/> must keep emitting
        /// <c>[nvarchar] (max)</c> for <see cref="string"/> properties (the historical default).
        /// Regression guard so the override branch never accidentally widens to all strings.
        /// </summary>
        [TestMethod]
        public void DefaultStringPropertyKeepsNvarcharMax()
        {
            var cache = new InsertBatchTypeFieldCache<DefaultStringEntity>();
            var info = cache.PropertyMappingInfo.Single(p => p.SqlInfo.FieldName == "default_string");

            Assert.AreEqual(DefaultStringSqlType, info.SqlInfo.SqlColDefinition,
                $"Default string property must remain {DefaultStringSqlType} when SqlTypeOverride is not set.");
        }

        /// <summary>
        /// A property with <see cref="ColumnAttribute.SqlTypeOverride"/> set must emit exactly
        /// that string as its column type definition, replacing the inferred <c>nvarchar(max)</c>.
        /// </summary>
        [TestMethod]
        public void StringPropertyWithSqlTypeOverrideUsesOverride()
        {
            var cache = new InsertBatchTypeFieldCache<OverriddenStringEntity>();
            var info = cache.PropertyMappingInfo.Single(p => p.SqlInfo.FieldName == "overridden_string");

            Assert.AreEqual(ExpectedUrlSqlType, info.SqlInfo.SqlColDefinition,
                "SqlTypeOverride must replace the default nvarchar(max) emission for string properties.");
        }

        /// <summary>
        /// <see cref="ColumnAttribute.SqlTypeOverride"/> must be ignored for non-string CLR types -
        /// the inferred type (e.g. <c>int</c>) stays authoritative. This stops misuse from silently
        /// breaking schema generation for typed columns.
        /// </summary>
        [TestMethod]
        public void NonStringPropertyIgnoresSqlTypeOverride()
        {
            var cache = new InsertBatchTypeFieldCache<OverriddenNonStringEntity>();
            var info = cache.PropertyMappingInfo.Single(p => p.SqlInfo.FieldName == "int_with_bogus_override");

            Assert.AreEqual("int", info.SqlInfo.SqlColDefinition,
                "SqlTypeOverride must not affect non-string properties; inferred type wins.");
        }

        /// <summary>
        /// The App Insights hit-import staging entity's <c>url</c> column joins
        /// <c>urls.full_url</c> in "Migrate Hits Import into Hits.sql". After PR #108
        /// shrank <c>urls.full_url</c> to <c>varchar(1700)</c>, the staging column must be the
        /// same type or the join falls back to an implicit conversion that ignores
        /// <c>IX_urls_full_url</c>.
        /// </summary>
        [TestMethod]
        public void HitTempEntityUrlColumnIsVarchar1700()
        {
            var cache = new InsertBatchTypeFieldCache<HitTempEntity>();
            var info = cache.PropertyMappingInfo.Single(p => p.SqlInfo.FieldName == "url");

            Assert.AreEqual(ExpectedUrlSqlType, info.SqlInfo.SqlColDefinition,
                "##import_staging_hit_imports.url must match urls.full_url (varchar(1700)) so IX_urls_full_url is usable.");
        }

        /// <summary>
        /// The Office 365 activity staging entity's <c>object_id</c> column joins
        /// <c>urls.full_url</c> in "Insert Activity from Staging Table.sql". Same constraint
        /// as <see cref="HitTempEntityUrlColumnIsVarchar1700"/>: must be <c>varchar(1700)</c>
        /// post-PR-#108 to keep the index usable.
        /// </summary>
        [TestMethod]
        public void AuditLogTempEntityObjectIdColumnIsVarchar1700()
        {
            var cache = new InsertBatchTypeFieldCache<AuditLogTempEntity>();
            var info = cache.PropertyMappingInfo.Single(p => p.SqlInfo.FieldName == "object_id");

            Assert.AreEqual(ExpectedUrlSqlType, info.SqlInfo.SqlColDefinition,
                "##import_staging_event_lookups.object_id must match urls.full_url (varchar(1700)) so IX_urls_full_url is usable.");
        }

        /// <summary>
        /// The AppInsights clicks staging entity's <c>url</c> column joins <c>urls.full_url</c>
        /// in "Migrate clicks from staging.sql". Must be <c>varchar(1700)</c> for the index to
        /// be usable. Without this, the implicit conversion of <c>urls.full_url</c> to
        /// <c>nvarchar</c> defeats <c>IX_urls_full_url</c> on every clicks merge.
        /// </summary>
        [TestMethod]
        public void ClickTempEntityUrlColumnIsVarchar1700()
        {
            var cache = new InsertBatchTypeFieldCache<ClickTempEntity>();
            var info = cache.PropertyMappingInfo.Single(p => p.SqlInfo.FieldName == "url");

            Assert.AreEqual(ExpectedUrlSqlType, info.SqlInfo.SqlColDefinition,
                "##import_staging_clicks.url must match urls.full_url (varchar(1700)) so IX_urls_full_url is usable.");
        }

        /// <summary>
        /// The SharePoint Copilot events staging entity's <c>url</c> column joins
        /// <c>urls.full_url</c> in "insert_sp_copilot_events_from_staging_table.sql". Same
        /// constraint: must be <c>varchar(1700)</c> so the merge can use <c>IX_urls_full_url</c>.
        /// </summary>
        [TestMethod]
        public void SPCopilotLogTempEntityUrlColumnIsVarchar1700()
        {
            var cache = new InsertBatchTypeFieldCache<SPCopilotLogTempEntity>();
            var info = cache.PropertyMappingInfo.Single(p => p.SqlInfo.FieldName == "url");

            Assert.AreEqual(ExpectedUrlSqlType, info.SqlInfo.SqlColDefinition,
                "Copilot SP staging .url must match urls.full_url (varchar(1700)) so IX_urls_full_url is usable.");
        }

        // ---- Local test fixtures ----

        private class DefaultStringEntity
        {
            [Column("default_string")]
            public string DefaultString { get; set; }
        }

        private class OverriddenStringEntity
        {
            [Column("overridden_string", SqlTypeOverride = ExpectedUrlSqlType)]
            public string OverriddenString { get; set; }
        }

        private class OverriddenNonStringEntity
        {
            // Override should be ignored: int wins.
            [Column("int_with_bogus_override", SqlTypeOverride = "varchar(1700)")]
            public int IntWithBogusOverride { get; set; }
        }
    }
}
