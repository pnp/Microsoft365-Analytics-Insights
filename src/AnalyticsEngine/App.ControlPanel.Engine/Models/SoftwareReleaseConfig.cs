namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Where solution engine binaries are located
    /// </summary>
    public class SoftwareReleaseConfig
    {

        /// <summary>
        /// Storage account URL + SAS where to get software releases.
        /// </summary>
        public string SoftwareDownloadURL { get; set; } = string.Empty;

        /// <summary>
        /// Calculated prop from full URL
        /// </summary>
        public string ContainerName
        {
            get
            {
                // URL: https://spoinsights.blob.core.windows.net/v2downloads?sv=2020-04-08&amp;ss=b&amp;srt=sco&amp;st=2021-06-06T11%3A27%3A00Z&amp;se=2029-02-08T12%3A27%3A00Z&amp;sp=rl&amp;sig=xxxx
                const string DOMAIN = "blob.core.windows.net/";
                var domainStart = SoftwareDownloadURL.IndexOf(DOMAIN);
                if (domainStart > 0)
                {
                    var qsStart = SoftwareDownloadURL.IndexOf("?");
                    if (qsStart > -1)
                    {
                        var start = domainStart + DOMAIN.Length;
                        return SoftwareDownloadURL.Substring(start, qsStart - start);
                    }
                }
                return "";
            }
        }

        public string AccountBaseUrl
        {
            get
            {
                // URL: https://spoinsights.blob.core.windows.net/v2downloads?sv=2020-04-08&amp;ss=b&amp;srt=sco&amp;st=2021-06-06T11%3A27%3A00Z&amp;se=2029-02-08T12%3A27%3A00Z&amp;sp=rl&amp;sig=WLbftVXTlTzsGMdgge2Ey46XBJ07uTYcHurDIujernc%3D
                const string DOMAIN = "blob.core.windows.net";
                var domainStart = SoftwareDownloadURL.IndexOf(DOMAIN);
                if (domainStart > 0)
                {
                    return SoftwareDownloadURL.Substring(0, domainStart + DOMAIN.Length);

                }
                return "";
            }
        }

        public string SAS
        {
            get
            {
                // URL: https://spoinsights.blob.core.windows.net/v2downloads?sv=2020-04-08&amp;ss=b&amp;srt=sco&amp;st=2021-06-06T11%3A27%3A00Z&amp;se=2029-02-08T12%3A27%3A00Z&amp;sp=rl&amp;sig=WLbftVXTlTzsGMdgge2Ey46XBJ07uTYcHurDIujernc%3D

                var qsStart = SoftwareDownloadURL.IndexOf("?");
                if (qsStart > -1)
                {
                    return SoftwareDownloadURL.Substring(qsStart, SoftwareDownloadURL.Length - qsStart);
                }

                return "";
            }
        }

        public bool IsValid => !string.IsNullOrEmpty(SoftwareDownloadURL) && !string.IsNullOrEmpty(ContainerName);
    }
}
