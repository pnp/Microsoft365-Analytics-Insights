using System;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests.FakeEntities
{
    internal class DataGenerators
    {

        public static ExchangeAuditLogContent GetRandomExchangeLog(int lookupsMax)
        {
            ExchangeAuditLogContent log = new ExchangeAuditLogContent();
            log.Workload = ActivityImportConstants.WORKLOAD_EXCHANGE;
            log.Id = Guid.NewGuid();
            log.UserId = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.UserID);
            log.Operation = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.Operation);
            log.ObjectId = "TESTING.outlook.com/Microsoft Exchange Hosted Organizations/contoso.onmicrosoft.com/" + DataGenerators.GetRandomString(5);

            var dic = new Dictionary<string, string>();
            dic.Add("Name", "ThisExhcangeProp");
            dic.Add("Value", "ThisExhcangeVal");
            log.ExtendedProperties.Add(dic);
            int randomSeconds = random.Next(1, 999999);
            log.CreationTime = DateTime.Now.AddSeconds(randomSeconds - (randomSeconds * 2));

            return log;
        }
        public static ExchangeAuditLogContent GetRandomExchangeLog()
        {
            return GetRandomExchangeLog(0);
        }
        public static AbstractAuditLogContent GetRandomGeneralADLog(int lookupsMax)
        {
            GeneralAuditLogContent log = new GeneralAuditLogContent();
            log.Workload = ActivityImportConstants.WORKLOAD_DLP;
            log.Id = Guid.NewGuid();
            log.UserId = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.UserID);
            log.Operation = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.Operation);

            var dic = new Dictionary<string, string>();
            dic.Add("Name", "ThisADProp");
            dic.Add("Value", "ThisADVal");
            log.ExtendedProperties.Add(dic);

            int randomSeconds = random.Next(1, 999999);
            log.CreationTime = DateTime.Now.AddSeconds(randomSeconds - (randomSeconds * 2));

            return log;
        }
        public static AbstractAuditLogContent GetRandomGeneralADLog()
        {
            return GetRandomGeneralADLog(0);
        }
        public static AbstractAuditLogContent GetRandomAzureADLog(int lookupsMax)
        {
            AzureADAuditLogContent log = new AzureADAuditLogContent();
            log.Workload = ActivityImportConstants.WORKLOAD_AZURE_AD;
            log.Id = Guid.NewGuid();
            log.UserId = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.UserID);
            log.Operation = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.Operation);

            var dic = new Dictionary<string, string>();
            dic.Add("ThisAzureADProp", "ThisAzureADVal");
            log.ExtendedProperties.Add(dic);

            int randomSeconds = random.Next(1, 999999);
            log.CreationTime = DateTime.Now.AddSeconds(randomSeconds - (randomSeconds * 2));

            return log;
        }
        public static AbstractAuditLogContent GetRandomAzureADLog()
        {
            return GetRandomAzureADLog(0);
        }

        public static AbstractAuditLogContent GetRandomAnyLog()
        {
            return GetRandomAnyLog(0);
        }
        public static AbstractAuditLogContent GetRandomAnyLog(int lookups)
        {
            int typeIndex = logTypeRandom.Next(0, 4);
            if (typeIndex == 0)
            {
                return DataGenerators.GetRandomSharePointLog(lookups);
            }
            else if (typeIndex == 1)
            {
                return GetRandomExchangeLog(lookups);
            }
            else if (typeIndex == 2)
            {
                return GetRandomGeneralADLog(lookups);
            }
            else if (typeIndex == 3)
            {
                return GetRandomAzureADLog(lookups);
            }

            return DataGenerators.GetRandomSharePointLog(lookups);
        }
        private static Random logTypeRandom = new Random();

        // For generating random data
        protected static Random random = new Random();

        /// <summary>
        /// Generate testing random object
        /// </summary>
        public static SharePointAuditLogContent GetRandomSharePointLog(int lookupsMax)
        {
            SharePointAuditLogContent log = new SharePointAuditLogContent();
            log.Workload = ActivityImportConstants.WORKLOAD_SP;
            log.Id = Guid.NewGuid();
            log.UserId = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.UserID);
            log.Operation = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.Operation);
            log.SourceFileName = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.SourceFileName);
            log.SourceFileExtension = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.SourceFileExtension);

            // URL
            log.ObjectId = "https://sharepoint/sites/site/files/file" + DataGenerators.GetRandomString(50) + "/" + log.SourceFileExtension;
            int randomSeconds = random.Next(1, 999999);
            log.CreationTime = DateTime.Now.AddSeconds(randomSeconds - (randomSeconds * 2));
            log.SiteUrl = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.SiteURL);
            log.EventData = "Event data " + DataGenerators.GetRandomString(10);
            log.ItemType = DataGenerators.GetRandomLookup(lookupsMax, DataGenerators.LookupType.Type);

            return log;
        }
        public static SharePointAuditLogContent GetRandomSharePointLog()
        {
            return GetRandomSharePointLog(0);
        }

        public static string GetRandomLookup(int maxLookups, LookupType type)
        {
            string prefix = string.Empty;

            // Set prefix
            switch (type)
            {
                case LookupType.UserID:
                    prefix = "User";
                    break;
                case LookupType.Operation:
                    prefix = "Operation";
                    break;
                case LookupType.SourceFileName:
                    prefix = "File";
                    break;
                case LookupType.SourceFileExtension:
                    prefix = "ext";
                    break;
                case LookupType.Type:
                    prefix = "Type";
                    break;
                case LookupType.PageTitle:
                    prefix = "Page Title";
                    break;
                case LookupType.SiteURL:
                    prefix = "https://sharepoint/sites/site";
                    break;
                case LookupType.FileURL:
                    prefix = "https://sharepoint/sites/site"; // More added below
                    break;
                case LookupType.Browser:
                    prefix = "Browser";
                    break;
                case LookupType.OS:
                    prefix = "OS";
                    break;
                case LookupType.County:
                    prefix = "Province";
                    break;
                case LookupType.Country:
                    prefix = "Country";
                    break;
                case LookupType.City:
                    prefix = "City";
                    break;
                case LookupType.WebTitle:
                    prefix = "Web";
                    break;
                default:
                    throw new NotSupportedException("No idea what type");
            }

            string uniqueLookup = $"{prefix} {DataGenerators.GetRandomString(5)}";

            // Exceptions
            if (type == LookupType.FileURL)
            {
                uniqueLookup = $"/file-{DataGenerators.GetRandomString(5)}.{GetRandomLookup(maxLookups, LookupType.SourceFileExtension)}";
            }

            if (maxLookups == 0)
            {
                return uniqueLookup;
            }
            else if (maxLookups > 0)
            {
                // From cache
                List<string> lookupCache = LookupCaches.Value.Where(c => c.Key == type).SingleOrDefault().Value;

                // Build cache if not already populated
                if (lookupCache.Count < maxLookups)
                {
                    lookupCache.Add(uniqueLookup);
                    return uniqueLookup;
                }
                else
                {
                    // Return random 
                    return lookupCache[random.Next(0, maxLookups)];
                }
            }
            else
            {
                throw new InvalidOperationException("Unexpected max lookup count");
            }
        }

        // Lists of random lookups
        static Lazy<Dictionary<LookupType, List<string>>> LookupCaches = new Lazy<Dictionary<LookupType, List<string>>>(() =>
        {
            Dictionary<LookupType, List<string>> caches = new Dictionary<LookupType, List<string>>();

            // Add each type
            foreach (LookupType t in (LookupType[])Enum.GetValues(typeof(LookupType)))
            {
                caches.Add(t, new List<string>());
            }


            return caches;
        });

        public enum LookupType
        {
            Unknown,
            UserID,
            Operation,
            SourceFileName,
            SourceFileExtension,
            Type,
            PageTitle,
            SiteURL,
            FileURL,
            Country,
            County,
            City,
            Browser,
            OS,
            WebTitle
        }

        public static string GetRandomString(int length = 16)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, length)
              .Select(s => s[random.Next(s.Length)]).ToArray());
        }
    }

}
