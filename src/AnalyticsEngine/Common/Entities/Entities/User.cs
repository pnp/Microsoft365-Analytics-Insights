
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// User lookup for a session
    /// </summary>
    [Table("users")]
    public class User : AbstractEFEntity
    {
        public User() : base()
        {
            this.LicenseLookups = new List<UserLicenseTypeLookup>();
        }

        [Column("user_name")]
        public string UserPrincipalName { get; set; } = string.Empty;

        [Column("mail")]
        public string Mail { get; set; } = string.Empty;

        [Column("last_updated")]
        public DateTime? LastUpdated { get; set; } = null;

        [Column("azure_ad_id")]
        public string AzureAdId { get; set; } = string.Empty;

        [Column("account_enabled")]
        public bool? AccountEnabled { get; set; }

        [MaxLength(50)]
        [Column("postalcode")]
        public string PostalCode { get; set; } = string.Empty;

        #region Lookup Properties

        [ForeignKey(nameof(CompanyName))]
        [Column("company_name_id")]
        public int? CompanyNameId { get; set; } = null;

        public CompanyName CompanyName { get; set; }

        [ForeignKey(nameof(StateOrProvince))]
        [Column("state_or_province_id")]
        public int? StateOrProvinceId { get; set; } = null;

        public StateOrProvince StateOrProvince { get; set; }


        [ForeignKey(nameof(Manager))]
        [Column("manager_id")]
        public int? ManagerId { get; set; } = null;

        public User Manager { get; set; }

        [ForeignKey(nameof(UserCountry))]
        [Column("country_or_region_id")]
        public int? UserCountryId { get; set; } = null;

        public CountryOrRegion UserCountry { get; set; }


        [ForeignKey(nameof(OfficeLocation))]
        [Column("office_location_id")]
        public int? OfficeLocationId { get; set; } = null;

        public UserOfficeLocation OfficeLocation { get; set; }

        [ForeignKey(nameof(UsageLocation))]
        [Column("usage_location_id")]
        public int? UsageLocationId { get; set; } = null;

        public UserUsageLocation UsageLocation { get; set; }


        [ForeignKey(nameof(Department))]
        [Column("department_id")]
        public int? DepartmentId { get; set; } = null;

        public UserDepartment Department { get; set; }

        public List<UserLicenseTypeLookup> LicenseLookups { get; set; }


        [ForeignKey(nameof(JobTitle))]
        [Column("job_title_id")]
        public int? JobTitleId { get; set; } = null;

        public UserJobTitle JobTitle { get; set; }

        #endregion

        public override string ToString()
        {
            return $"{UserPrincipalName}";
        }
    }

    #region Lookup Tables

    [Table("user_departments")]
    public class UserDepartment : AbstractEFEntityWithName
    {
    }

    [Table("user_license_type_lookups")]
    public class UserLicenseTypeLookup : AbstractEFEntity
    {
        [ForeignKey(nameof(User))]
        [Column("user_id")]
        public int UserId { get; set; }

        public User User { get; set; }

        [ForeignKey(nameof(License))]
        [Column("license_type_id")]
        public int LicenseTypeId { get; set; }

        public LicenseType License { get; set; }

    }

    [Table("license_types")]
    public class LicenseType : AbstractEFEntityWithName
    {
        [Column("sku_id")]
        public string SKUID { get; set; }
    }

    [Table("user_job_titles")]
    public class UserJobTitle : AbstractEFEntityWithName
    {
    }

    [Table("user_office_locations")]
    public class UserOfficeLocation : AbstractEFEntityWithName
    {
    }

    [Table("user_usage_locations")]
    public class UserUsageLocation : AbstractEFEntityWithName
    {
    }

    [Table("user_state_or_province")]
    public class StateOrProvince : AbstractEFEntityWithName
    {
    }
    [Table("user_country_or_region")]
    public class CountryOrRegion : AbstractEFEntityWithName
    {
    }
    [Table("user_company_name")]
    public class CompanyName : AbstractEFEntityWithName
    {
    }
    #endregion
}
