using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for the AppConfig parsing rules tightened by
    /// PR #95 (collapsed bool.TryParse + default-preservation): missing or
    /// invalid AppSettings must not silently flip numeric configs to 0 or
    /// override constructor defaults.
    ///
    /// Note on test strategy: the AppSettings collection loaded by the test
    /// host is read-only, so we cannot Remove() keys. Setting a value to an
    /// unparseable string exercises the same fall-through default path that
    /// a missing key would, since AppConfig uses TryParse on the raw value.
    /// </summary>
    [TestClass]
    public class AppConfigDefaultsTests
    {
        private const string ChunkSize = "ChunkSize";
        private const string DaysBeforeNowToDownload = "DaysBeforeNowToDownload";
        private const string TimeChunkOverlapMinutes = "TimeChunkOverlapMinutes";
        private const string MetadataRefreshMinutes = "MetadataRefreshMinutes";
        private const string ForceUsageReportsImport = "ForceUsageReportsImport";
        private const string MaxSummaryFetchConcurrency = "MaxSummaryFetchConcurrency";
        private const string ImportAggressiveness = "ImportAggressiveness";
        private const string MaxAuditReportLoadConcurrency = "MaxAuditReportLoadConcurrency";
        private const string ImportCyclePauseMinutes = "ImportCyclePauseMinutes";
        private const string GraphMetadataImportIntervalHours = "GraphMetadataImportIntervalHours";
        private const string GraphTeamsImportIntervalHours = "GraphTeamsImportIntervalHours";
        private const string ForceGraphMetadataImport = "ForceGraphMetadataImport";
        private const string ImportStartStaggerMinutes = "ImportStartStaggerMinutes";

        private static readonly string[] _trackedKeys =
        {
            ChunkSize,
            DaysBeforeNowToDownload,
            TimeChunkOverlapMinutes,
            MetadataRefreshMinutes,
            ForceUsageReportsImport,
            MaxSummaryFetchConcurrency,
            ImportAggressiveness,
            MaxAuditReportLoadConcurrency,
            ImportCyclePauseMinutes,
            GraphMetadataImportIntervalHours,
            GraphTeamsImportIntervalHours,
            ForceGraphMetadataImport,
            ImportStartStaggerMinutes,
        };

        private Dictionary<string, string> _originalAppSettings;

        [TestInitialize]
        public void Init()
        {
            _originalAppSettings = new Dictionary<string, string>();
            foreach (var key in _trackedKeys)
            {
                _originalAppSettings[key] = ConfigurationManager.AppSettings[key];
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (_originalAppSettings == null)
            {
                return;
            }

            foreach (var kvp in _originalAppSettings)
            {
                // Restore each key to whatever it was before the test ran.
                // We cannot Remove a key when the section is read-only, so a
                // previously-missing key is restored as empty; AppConfig treats
                // both empty and missing as "no value" via its TryParse path.
                ConfigurationManager.AppSettings.Set(kvp.Key, kvp.Value ?? string.Empty);
            }
        }

        [TestMethod]
        public void ChunkSize_InvalidAppSetting_KeepsOneDayDefault()
        {
            ConfigurationManager.AppSettings.Set(ChunkSize, "not-a-timespan");
            var cfg = new AppConfig();
            Assert.AreEqual(TimeSpan.FromDays(1), cfg.ChunkSize,
                "ChunkSize must fall back to 1 day when the AppSetting is invalid, not TimeSpan.Zero.");
        }

        [TestMethod]
        public void ChunkSize_EmptyAppSetting_KeepsOneDayDefault()
        {
            ConfigurationManager.AppSettings.Set(ChunkSize, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(TimeSpan.FromDays(1), cfg.ChunkSize,
                "An empty ChunkSize setting must not silently parse to TimeSpan.Zero.");
        }

        [TestMethod]
        public void ChunkSize_ValidAppSetting_IsParsed()
        {
            ConfigurationManager.AppSettings.Set(ChunkSize, "0.02:00:00");
            var cfg = new AppConfig();
            Assert.AreEqual(TimeSpan.FromHours(2), cfg.ChunkSize);
        }

        [TestMethod]
        public void DaysBeforeNowToDownload_InvalidAppSetting_KeepsSixDefault()
        {
            ConfigurationManager.AppSettings.Set(DaysBeforeNowToDownload, "not-an-int");
            var cfg = new AppConfig();
            Assert.AreEqual(6, cfg.DaysBeforeNowToDownload,
                "DaysBeforeNowToDownload must fall back to 6 when the AppSetting is invalid, not 0.");
        }

        [TestMethod]
        public void DaysBeforeNowToDownload_EmptyAppSetting_KeepsSixDefault()
        {
            ConfigurationManager.AppSettings.Set(DaysBeforeNowToDownload, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(6, cfg.DaysBeforeNowToDownload);
        }

        [TestMethod]
        public void TimeChunkOverlapMinutes_InvalidAppSetting_KeepsFiveDefault()
        {
            ConfigurationManager.AppSettings.Set(TimeChunkOverlapMinutes, "abc");
            var cfg = new AppConfig();
            Assert.AreEqual(5, cfg.TimeChunkOverlapMinutes,
                "TimeChunkOverlapMinutes must fall back to 5 when the AppSetting is invalid, not 0.");
        }

        [TestMethod]
        public void TimeChunkOverlapMinutes_EmptyAppSetting_KeepsFiveDefault()
        {
            ConfigurationManager.AppSettings.Set(TimeChunkOverlapMinutes, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(5, cfg.TimeChunkOverlapMinutes);
        }

        [TestMethod]
        public void MetadataRefreshMinutes_InvalidAppSetting_KeepsTwentyFourHourDefault()
        {
            ConfigurationManager.AppSettings.Set(MetadataRefreshMinutes, "nope");
            var cfg = new AppConfig();
            Assert.AreEqual(24 * 60, cfg.MetadataRefreshMinutes,
                "MetadataRefreshMinutes must keep its 24-hour property default when the AppSetting is invalid.");
        }

        [TestMethod]
        public void MetadataRefreshMinutes_NegativeAppSetting_KeepsTwentyFourHourDefault()
        {
            ConfigurationManager.AppSettings.Set(MetadataRefreshMinutes, "-1");
            var cfg = new AppConfig();
            Assert.AreEqual(24 * 60, cfg.MetadataRefreshMinutes,
                "Negative refresh intervals are rejected; default must be preserved.");
        }

        [TestMethod]
        public void MaxSummaryFetchConcurrency_InvalidAppSetting_DefaultsToEight()
        {
            ConfigurationManager.AppSettings.Set(MaxSummaryFetchConcurrency, "abc");
            var cfg = new AppConfig();
            Assert.AreEqual(8, cfg.MaxSummaryFetchConcurrency);
        }

        [TestMethod]
        public void MaxSummaryFetchConcurrency_EmptyAppSetting_DefaultsToEight()
        {
            ConfigurationManager.AppSettings.Set(MaxSummaryFetchConcurrency, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(8, cfg.MaxSummaryFetchConcurrency);
        }

        [TestMethod]
        public void MaxSummaryFetchConcurrency_ZeroOrNegative_DefaultsToEight()
        {
            ConfigurationManager.AppSettings.Set(MaxSummaryFetchConcurrency, "0");
            Assert.AreEqual(8, new AppConfig().MaxSummaryFetchConcurrency,
                "0 disables concurrency entirely; default of 8 must be preserved.");

            ConfigurationManager.AppSettings.Set(MaxSummaryFetchConcurrency, "-3");
            Assert.AreEqual(8, new AppConfig().MaxSummaryFetchConcurrency,
                "Negative concurrency is invalid; default of 8 must be preserved.");
        }

        [TestMethod]
        public void MaxSummaryFetchConcurrency_PositiveAppSetting_IsParsed()
        {
            ConfigurationManager.AppSettings.Set(MaxSummaryFetchConcurrency, "16");
            var cfg = new AppConfig();
            Assert.AreEqual(16, cfg.MaxSummaryFetchConcurrency);
        }

        [TestMethod]
        public void ForceUsageReportsImport_EmptyAppSetting_DefaultsToFalse()
        {
            ConfigurationManager.AppSettings.Set(ForceUsageReportsImport, string.Empty);
            var cfg = new AppConfig();
            Assert.IsFalse(cfg.ForceUsageReportsImport,
                "ForceUsageReportsImport must default to false when the AppSetting is empty.");
        }

        [TestMethod]
        public void ForceUsageReportsImport_TrueAppSetting_IsParsed()
        {
            ConfigurationManager.AppSettings.Set(ForceUsageReportsImport, "true");
            var cfg = new AppConfig();
            Assert.IsTrue(cfg.ForceUsageReportsImport);
        }

        [TestMethod]
        public void ForceUsageReportsImport_InvalidAppSetting_DefaultsToFalse()
        {
            ConfigurationManager.AppSettings.Set(ForceUsageReportsImport, "garbage");
            var cfg = new AppConfig();
            Assert.IsFalse(cfg.ForceUsageReportsImport,
                "Unparseable values must not flip ForceUsageReportsImport to true.");
        }

        // ----- Import aggressiveness preset & cadence knobs (issue #161) -----

        [TestMethod]
        public void ImportAggressiveness_EmptyAppSetting_DefaultsToBalanced()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, string.Empty);
            Assert.AreEqual(ImportAggressivenessLevel.Balanced, new AppConfig().ImportAggressiveness);
        }

        [TestMethod]
        public void ImportAggressiveness_InvalidAppSetting_DefaultsToBalanced()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "turbo");
            Assert.AreEqual(ImportAggressivenessLevel.Balanced, new AppConfig().ImportAggressiveness);
        }

        [TestMethod]
        public void ImportAggressiveness_IsCaseInsensitive()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "gEnTlE");
            Assert.AreEqual(ImportAggressivenessLevel.Gentle, new AppConfig().ImportAggressiveness);
        }

        [TestMethod]
        public void BalancedPreset_SetsModeratedDefaults()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "Balanced");
            ConfigurationManager.AppSettings.Set(MaxAuditReportLoadConcurrency, string.Empty);
            ConfigurationManager.AppSettings.Set(ImportCyclePauseMinutes, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(8, cfg.MaxAuditReportLoadConcurrency, "Balanced audit-load concurrency should be 8.");
            Assert.AreEqual(10, cfg.ImportCyclePauseMinutes, "Balanced cycle pause should be 10 min.");
        }

        [TestMethod]
        public void HighPreset_SetsLegacyDefaults()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "High");
            ConfigurationManager.AppSettings.Set(MaxAuditReportLoadConcurrency, string.Empty);
            ConfigurationManager.AppSettings.Set(ImportCyclePauseMinutes, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(20, cfg.MaxAuditReportLoadConcurrency, "High audit-load concurrency should be 20 (legacy).");
            Assert.AreEqual(10, cfg.ImportCyclePauseMinutes);
        }

        [TestMethod]
        public void GentlePreset_SetsLowCpuDefaults()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "Gentle");
            ConfigurationManager.AppSettings.Set(MaxAuditReportLoadConcurrency, string.Empty);
            ConfigurationManager.AppSettings.Set(ImportCyclePauseMinutes, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(3, cfg.MaxAuditReportLoadConcurrency, "Gentle audit-load concurrency should be 3.");
            Assert.AreEqual(20, cfg.ImportCyclePauseMinutes, "Gentle cycle pause should be 20 min.");
        }

        [TestMethod]
        public void ExplicitMaxAuditReportLoadConcurrency_OverridesPreset()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "Gentle");
            ConfigurationManager.AppSettings.Set(MaxAuditReportLoadConcurrency, "12");
            Assert.AreEqual(12, new AppConfig().MaxAuditReportLoadConcurrency,
                "An explicit MaxAuditReportLoadConcurrency must override the preset.");
        }

        [TestMethod]
        public void MaxAuditReportLoadConcurrency_ZeroOrInvalid_FallsBackToPreset()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "Balanced");
            ConfigurationManager.AppSettings.Set(MaxAuditReportLoadConcurrency, "0");
            Assert.AreEqual(8, new AppConfig().MaxAuditReportLoadConcurrency, "0 is invalid; preset (8) preserved.");

            ConfigurationManager.AppSettings.Set(MaxAuditReportLoadConcurrency, "abc");
            Assert.AreEqual(8, new AppConfig().MaxAuditReportLoadConcurrency, "Unparseable; preset (8) preserved.");
        }

        [TestMethod]
        public void ExplicitImportCyclePauseMinutes_OverridesPreset()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "High");
            ConfigurationManager.AppSettings.Set(ImportCyclePauseMinutes, "30");
            Assert.AreEqual(30, new AppConfig().ImportCyclePauseMinutes);
        }

        [TestMethod]
        public void GraphMetadataImportIntervalHours_EmptyOrInvalid_DefaultsTo24()
        {
            ConfigurationManager.AppSettings.Set(GraphMetadataImportIntervalHours, string.Empty);
            Assert.AreEqual(24, new AppConfig().GraphMetadataImportIntervalHours);

            ConfigurationManager.AppSettings.Set(GraphMetadataImportIntervalHours, "nope");
            Assert.AreEqual(24, new AppConfig().GraphMetadataImportIntervalHours);
        }

        [TestMethod]
        public void GraphMetadataImportIntervalHours_ZeroIsAllowed()
        {
            ConfigurationManager.AppSettings.Set(GraphMetadataImportIntervalHours, "0");
            Assert.AreEqual(0, new AppConfig().GraphMetadataImportIntervalHours,
                "0 explicitly disables the gate and must be honoured.");
        }

        [TestMethod]
        public void GraphMetadataImportIntervalHours_PositiveIsParsed()
        {
            ConfigurationManager.AppSettings.Set(GraphMetadataImportIntervalHours, "6");
            Assert.AreEqual(6, new AppConfig().GraphMetadataImportIntervalHours);
        }

        [TestMethod]
        public void ImportStartStaggerMinutes_EmptyOrInvalid_DefaultsToHalfCyclePause()
        {
            // Gentle => pause 20 => default stagger 10
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "Gentle");
            ConfigurationManager.AppSettings.Set(ImportCyclePauseMinutes, string.Empty);
            ConfigurationManager.AppSettings.Set(ImportStartStaggerMinutes, string.Empty);
            Assert.AreEqual(10, new AppConfig().ImportStartStaggerMinutes,
                "Default stagger should be half the (Gentle=20) cycle pause.");
        }

        [TestMethod]
        public void ImportStartStaggerMinutes_ExplicitAndZeroHonoured()
        {
            ConfigurationManager.AppSettings.Set(ImportStartStaggerMinutes, "7");
            Assert.AreEqual(7, new AppConfig().ImportStartStaggerMinutes);

            ConfigurationManager.AppSettings.Set(ImportStartStaggerMinutes, "0");
            Assert.AreEqual(0, new AppConfig().ImportStartStaggerMinutes, "0 disables stagger and must be honoured.");
        }

        [TestMethod]
        public void HighPreset_DisablesNonFreshGraphGate()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "High");
            ConfigurationManager.AppSettings.Set(GraphMetadataImportIntervalHours, string.Empty);
            ConfigurationManager.AppSettings.Set(GraphTeamsImportIntervalHours, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(0, cfg.GraphMetadataImportIntervalHours, "High must run metadata/apps every cycle (legacy).");
            Assert.AreEqual(0, cfg.GraphTeamsImportIntervalHours, "High must run Teams every cycle (legacy).");
        }

        [TestMethod]
        public void BalancedPreset_DailyGatesNonFreshGraph()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "Balanced");
            ConfigurationManager.AppSettings.Set(GraphMetadataImportIntervalHours, string.Empty);
            ConfigurationManager.AppSettings.Set(GraphTeamsImportIntervalHours, string.Empty);
            var cfg = new AppConfig();
            Assert.AreEqual(24, cfg.GraphMetadataImportIntervalHours);
            Assert.AreEqual(24, cfg.GraphTeamsImportIntervalHours);
        }

        [TestMethod]
        public void GraphTeamsImportIntervalHours_ZeroAndExplicitHonoured()
        {
            ConfigurationManager.AppSettings.Set(ImportAggressiveness, "Balanced");
            ConfigurationManager.AppSettings.Set(GraphTeamsImportIntervalHours, "0");
            Assert.AreEqual(0, new AppConfig().GraphTeamsImportIntervalHours, "0 disables the Teams gate.");

            ConfigurationManager.AppSettings.Set(GraphTeamsImportIntervalHours, "6");
            Assert.AreEqual(6, new AppConfig().GraphTeamsImportIntervalHours);
        }

        [TestMethod]
        public void ForceGraphMetadataImport_DefaultsFalse_ParsesTrue_IgnoresGarbage()
        {
            ConfigurationManager.AppSettings.Set(ForceGraphMetadataImport, string.Empty);
            Assert.IsFalse(new AppConfig().ForceGraphMetadataImport);

            ConfigurationManager.AppSettings.Set(ForceGraphMetadataImport, "true");
            Assert.IsTrue(new AppConfig().ForceGraphMetadataImport);

            ConfigurationManager.AppSettings.Set(ForceGraphMetadataImport, "garbage");
            Assert.IsFalse(new AppConfig().ForceGraphMetadataImport, "Unparseable must not flip to true.");
        }
    }
}
