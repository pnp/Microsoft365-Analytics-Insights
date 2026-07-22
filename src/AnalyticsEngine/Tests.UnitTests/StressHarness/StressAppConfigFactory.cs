using Common.Entities;
using Common.Entities.Config;
using System;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// Builds an <see cref="AppConfig"/> for the DB-backed stress harness without touching real
    /// configuration. Uses <c>FormatterServices.GetUninitializedObject</c> to bypass base-constructor
    /// validation (mirrors the FakeDataGen factory), then sets only the fields the audit-import path
    /// reads: the activity-window settings and (via <see cref="AppConnectionStrings"/>) the DB connection.
    /// Graph credentials are placeholders - they never authenticate for SharePoint-only events.
    /// </summary>
    public static class StressAppConfigFactory
    {
        public static AppConfig Create(string databaseConnectionString, int windowDays = 6)
        {
            var config = (AppConfig)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AppConfig));
            config.TenantGUID = Guid.Parse("00000000-0000-0000-0000-000000000001");
            config.ClientID = "fake-client-id-for-stress-testing";
            config.ClientSecret = "fake-client-secret";
            config.KeyVaultUrl = "https://fake-keyvault.vault.azure.net/";
            config.TenantDomain = "contoso.onmicrosoft.com";
            config.AADInstance = "https://login.microsoftonline.com/";
            config.UseClientCertificate = false;

            // Activity import window - must yield >= 1 time-chunk or the content loader is never called, and
            // must be >= the data's WindowDays so the run-scoped dedup cache (sized by DaysBeforeNowToDownload)
            // covers every generated event (else WARM would re-import events older than the cache window).
            config.DaysBeforeNowToDownload = Math.Max(7, windowDays + 1);
            config.TimeChunkOverlapMinutes = 5;
            config.ChunkSize = TimeSpan.FromDays(1);
            config.ContentTypesString = "Audit.SharePoint";

            config.WebAppURL = "https://fake-webapp.azurewebsites.net";
            config.BuildLabel = "stress-test";
            config.MetadataRefreshMinutes = 24 * 60;

            var connStrings = (AppConnectionStrings)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AppConnectionStrings));
            connStrings.DatabaseConnectionString = databaseConnectionString;
            connStrings.RedisConnectionString = "fake:6380,******";
            connStrings.ServiceBusConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=fake;SharedAccessKey=fake";
            // Empty so the blob-checkpoint factory deterministically selects the in-memory store (there is no
            // real Azure Table in the load test).
            connStrings.StorageConnectionString = "";
            config.ConnectionStrings = connStrings;

            config.ImportJobSettings = new ImportTaskSettings();

            return config;
        }
    }
}
