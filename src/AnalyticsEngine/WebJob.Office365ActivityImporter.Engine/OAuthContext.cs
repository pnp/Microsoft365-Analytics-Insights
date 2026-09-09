using DataUtils.Http;
using Microsoft.Extensions.Logging;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Context for Graph calls
    /// </summary>
    public class GraphAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public GraphAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://graph.microsoft.com/.default";
    }

    /// <summary>
    /// Context for Activity API calls
    /// </summary>
    public class ActivityAPIAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public ActivityAPIAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://manage.office.com/.default";
    }

    /// <summary>
    /// Context for Power Platform API calls (Copilot Studio credit consumption).
    /// </summary>
    /// <remarks>
    /// A token for this audience is not enough on its own: the service principal must also hold a Power
    /// Platform RBAC role (Power Platform reader is the least-privilege option) assigned at tenant scope.
    /// Without it the licensing routes answer 401/403 even though the token itself is valid, which is why the
    /// credit importer turns those two status codes into an explicit instruction rather than a generic error.
    /// </remarks>
    public class PowerPlatformAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public PowerPlatformAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://api.powerplatform.com/.default";
    }

    /// <summary>
    /// Context for Azure Resource Manager calls (Microsoft Cost Management).
    /// </summary>
    /// <remarks>
    /// As with Power Platform, the token is only half the story: the service principal needs the Cost
    /// Management Reader role at the scope being queried. That is an Azure RBAC assignment for subscription
    /// and management-group scopes, but is granted in the billing portal for EA and MCA billing-account
    /// scopes.
    /// </remarks>
    public class AzureManagementAppIndentityOAuthContext : ImportAppIndentityOAuthContext
    {
        public AzureManagementAppIndentityOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate) :
            base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        { }

        public override string ResourceURL => "https://management.azure.com/.default";
    }
}
