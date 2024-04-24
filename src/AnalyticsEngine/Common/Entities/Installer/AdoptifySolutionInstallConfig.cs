using System.Collections.Generic;

namespace Common.Entities.Installer
{

    public class AdoptifySolutionInstallConfig : BaseConfig
    {
        public string ExistingSiteUrl { get; set; }

        public bool ProvisionSchema { get; set; } = true;
        public bool CreateDefaultData { get; set; } = true;

        public override List<string> ValidatInputAndGetErrors()
        {
            var errs = new List<string>();
            if (!IsValidSPSiteCollectionURL(ExistingSiteUrl))
            {
                errs.Add("SharePoint site-collection URL for Adoptify must be valid, with no trailing slash");
            }
            return errs;
        }
    }
}
