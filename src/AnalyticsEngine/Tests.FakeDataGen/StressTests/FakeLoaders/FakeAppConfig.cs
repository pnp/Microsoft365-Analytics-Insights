using Common.Entities;
using Common.Entities.Config;
using System;

namespace Tests.FakeDataGen.StressTests.FakeLoaders
{
    /// <summary>
    /// Fake AppConfig for stress testing. Uses reflection to bypass base constructor validation.
    /// </summary>
    public static class FakeAppConfigFactory
    {
        public static AppConfig Create()
        {
            // Create an uninitialized instance without calling constructor
            var config = (AppConfig)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AppConfig));

            // Now set all properties manually
            config.TenantGUID = Guid.Parse("00000000-0000-0000-0000-000000000001");
            config.ClientID = "fake-client-id-for-stress-testing";
            config.ClientSecret = "fake-client-secret";
            config.KeyVaultUrl = "https://fake-keyvault.vault.azure.net/";
            config.TenantDomain = "fake.onmicrosoft.com";
            config.AADInstance = "https://login.microsoftonline.com/";
            config.UseClientCertificate = false;

            // Set reasonable defaults for activity import
            config.DaysBeforeNowToDownload = 7;
            config.TimeChunkOverlapMinutes = 5;
            config.ChunkSize = TimeSpan.FromDays(1);
            config.ContentTypesString = "Audit.SharePoint;Audit.Exchange";

            // Set other optional properties to safe defaults
            config.WebAppURL = "https://fake-webapp.azurewebsites.net";
            config.BuildLabel = "stress-test";
            config.MetadataRefreshMinutes = 24 * 60; // 24 hours

            // Create minimal connection strings using same technique
            var connStrings = (AppConnectionStrings)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AppConnectionStrings));
            connStrings.DatabaseConnectionString = "Server=fake;Database=fake;User Id=fake;Password=fake;";
            connStrings.RedisConnectionString = "fake:6380,password=fake,ssl=True,abortConnect=False";
            connStrings.ServiceBusConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=fake;SharedAccessKey=fake";
            connStrings.StorageConnectionString = "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=fake;EndpointSuffix=core.windows.net";

            config.ConnectionStrings = connStrings;

            // Import settings with defaults
            config.ImportJobSettings = new ImportTaskSettings();

            return config;
        }
    }
}
