using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// A license assigned to a user (from user_license_type_lookups -> license_types).
    /// </summary>
    public class UserLicenseModel
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuId")]
        public string SkuId { get; set; }
    }

    /// <summary>
    /// The "users" row plus its de-normalised lookup values for one user.
    /// </summary>
    public class UserProfileModel
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        [JsonProperty("azureAdId")]
        public string AzureAdId { get; set; }

        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }

        [JsonProperty("lastUpdated")]
        public DateTime? LastUpdated { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        [JsonProperty("companyName")]
        public string CompanyName { get; set; }

        [JsonProperty("countryOrRegion")]
        public string CountryOrRegion { get; set; }

        [JsonProperty("officeLocation")]
        public string OfficeLocation { get; set; }

        [JsonProperty("usageLocation")]
        public string UsageLocation { get; set; }

        [JsonProperty("stateOrProvince")]
        public string StateOrProvince { get; set; }

        [JsonProperty("postalCode")]
        public string PostalCode { get; set; }

        [JsonProperty("managerUserPrincipalName")]
        public string ManagerUserPrincipalName { get; set; }

        [JsonProperty("licenses")]
        public List<UserLicenseModel> Licenses { get; set; } = new List<UserLicenseModel>();
    }

    /// <summary>
    /// One category of data held for a user, with a record count and whether drill-down is available.
    /// </summary>
    public class UserDataCategoryModel
    {
        [JsonProperty("key")]
        public string Key { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("supportsDetail")]
        public bool SupportsDetail { get; set; }
    }

    /// <summary>
    /// Profile + per-category counts for a user.
    /// </summary>
    public class UserDataSummaryModel
    {
        [JsonProperty("profile")]
        public UserProfileModel Profile { get; set; }

        [JsonProperty("categories")]
        public List<UserDataCategoryModel> Categories { get; set; } = new List<UserDataCategoryModel>();
    }

    /// <summary>
    /// A single drill-down row for a category (most recent records first).
    /// </summary>
    public class UserDataDetailRowModel
    {
        [JsonProperty("timestamp")]
        public DateTime? Timestamp { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("detail")]
        public string Detail { get; set; }
    }

    /// <summary>
    /// The most recent rows for one category for a user.
    /// </summary>
    public class UserDataDetailResponseModel
    {
        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("totalCount")]
        public int TotalCount { get; set; }

        [JsonProperty("returnedCount")]
        public int ReturnedCount { get; set; }

        [JsonProperty("rows")]
        public List<UserDataDetailRowModel> Rows { get; set; } = new List<UserDataDetailRowModel>();
    }

    /// <summary>
    /// Simple error envelope ({ "message": "..." }) consumed by the SPA's fetch helper.
    /// </summary>
    public class ApiErrorModel
    {
        [JsonProperty("message")]
        public string Message { get; set; }

        public ApiErrorModel(string message)
        {
            Message = message;
        }
    }
}
