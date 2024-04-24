using Common.Entities.Installer;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.Entities
{
    /// <summary>
    /// SharePoint configuration for SPO Insights
    /// </summary>
    public class SharePointInstallConfig : BaseConfig
    {
        #region Constructors

        internal SharePointInstallConfig()
        {
            this.TargetSites = new List<string>();
        }
        public SharePointInstallConfig(string destinationDocLibTitle, string aiTrackerFileName,
            List<string> targetSites, string appCatalogSite) : this()
        {
            this.AppCatalogueURL = appCatalogSite;
            DestinationDocLibTitle = destinationDocLibTitle;
            AITrackerFileName = aiTrackerFileName;
            TargetSites = targetSites;
        }
        #endregion

        #region Properties


        public string AppCatalogueURL { get; set; }


        public string DestinationDocLibTitle { get; set; }

        public string AITrackerFileName { get; set; }
        public List<string> TargetSites { get; set; }


        #endregion

        /// <summary>
        /// Get default new config
        /// </summary>
        internal static SharePointInstallConfig Empty()
        {
            SharePointInstallConfig sharePointInstallConfig = new SharePointInstallConfig();
            sharePointInstallConfig.DestinationDocLibTitle = "SPOInsights";
            sharePointInstallConfig.AITrackerFileName = "AITracker.js";
            return sharePointInstallConfig;
        }

        public override List<string> ValidatInputAndGetErrors()
        {
            var errs = new List<string>();

            // URLs

            if (this.TargetSites.Count == 0)
            {
                errs.Add("Enter at least one site-collection URL to install to.");
            }
            else
            {
                foreach (var url in this.TargetSites)
                {
                    if (!IsValidSPSiteCollectionURL(url))
                    {
                        errs.Add("SharePoint site-collection URLs must be valid, with no trailing slash");
                        break;
                    }
                }
            }

            if (!IsValidSPSiteCollectionURL(this.AppCatalogueURL))
            {
                errs.Add("SharePoint app-catalog site-collection URL must be valid, with no trailing slash");
            }


            // Doc lib
            if (!string.IsNullOrWhiteSpace(this.DestinationDocLibTitle))
            {
                bool isValidName = System.Text.RegularExpressions.Regex.IsMatch(
                    this.DestinationDocLibTitle, @"^[a-zA-Z0-9_\s-]+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!isValidName)
                {
                    errs.Add("Enter a valid document-library name. Alphanumerics & spaces only.");
                }
            }
            else
            {
                errs.Add("Enter a document-library name.");
            }

            // Filename
            if (!string.IsNullOrWhiteSpace(this.AITrackerFileName))
            {
                bool isValidName = System.Text.RegularExpressions.Regex.IsMatch(
                    this.AITrackerFileName, @"^[\w,\s-]+\.[A-Za-z]{2}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!isValidName)
                {
                    errs.Add("Enter a valid AITracker name. Alphanumerics + '.js'.");
                }
            }
            else
            {
                errs.Add("Enter an AITracker filename.");
            }

            return errs;
        }
    }
}
