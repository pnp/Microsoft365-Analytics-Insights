using Common.Entities;
using Common.Entities.LookupCaches;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Metadata cache for user records
    /// </summary>
    internal class UserMetadataCache
    {
        private UserDepartmentCache departmentCache;
        private UserJobTitleCache jobTitleCache;
        private OfficeLocationCache officeLocationCache;
        private UsageLocationCache useageLocationCache;
        private LicenseTypeCache licenseTypeCache;
        private StateOrProvinceCache stateOrProvinceCache;
        private CountryOrRegionCache countryOrRegionCache;
        private CompanyNameCache companyNameCache;
        private UserCache userCache;

        public UserDepartmentCache DepartmentCache { get => departmentCache; set => departmentCache = value; }
        public UserJobTitleCache JobTitleCache { get => jobTitleCache; set => jobTitleCache = value; }
        public OfficeLocationCache OfficeLocationCache { get => officeLocationCache; set => officeLocationCache = value; }
        public UsageLocationCache UseageLocationCache { get => useageLocationCache; set => useageLocationCache = value; }
        public LicenseTypeCache LicenseTypeCache { get => licenseTypeCache; set => licenseTypeCache = value; }
        public StateOrProvinceCache StateOrProvinceCache { get => stateOrProvinceCache; set => stateOrProvinceCache = value; }
        public CountryOrRegionCache CountryOrRegionCache { get => countryOrRegionCache; set => countryOrRegionCache = value; }
        public CompanyNameCache CompanyNameCache { get => companyNameCache; set => companyNameCache = value; }
        public UserCache UserCache { get => userCache; set => userCache = value; }

        public UserMetadataCache(AnalyticsEntitiesContext context)
        {
            this.DepartmentCache = new UserDepartmentCache(context);
            this.JobTitleCache = new UserJobTitleCache(context);
            this.OfficeLocationCache = new OfficeLocationCache(context);
            this.UseageLocationCache = new UsageLocationCache(context);
            this.LicenseTypeCache = new LicenseTypeCache(context);

            this.LicenseTypeCache = new LicenseTypeCache(context);
            this.StateOrProvinceCache = new StateOrProvinceCache(context);
            this.CountryOrRegionCache = new CountryOrRegionCache(context);
            this.CompanyNameCache = new CompanyNameCache(context);
            this.UserCache = new UserCache(context);

        }
    }
}
