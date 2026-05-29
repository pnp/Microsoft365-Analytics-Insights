using Azure;
using Azure.Core;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class StorageAccountInstallTask : InstallTaskInAzResourceGroup<StorageAccountResource>
    {
        private readonly bool _allowPublicAccess;

        public StorageAccountInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create storage account";

        public async override Task<StorageAccountResource> ExecuteTaskReturnResult(object contextArg)
        {
            var name = _config.GetNameConfigValue();

            StorageAccountResource storageAccount = null;
            try
            {
                var accRepsonse = await Container.GetStorageAccountAsync(name);
                storageAccount = accRepsonse.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Ignore
            }

            if (storageAccount == null)
            {
                var newAccountInfo = new StorageAccountCreateOrUpdateContent(new StorageSku("Standard_LRS"), StorageKind.StorageV2, AzureLocation)
                {
                    MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2,
                    PublicNetworkAccess = _allowPublicAccess ? StoragePublicNetworkAccess.Enabled : StoragePublicNetworkAccess.Disabled
                };
                EnsureTagsOnNew(newAccountInfo.Tags);
                var storageAccountReq = await Container.GetStorageAccounts().CreateOrUpdateAsync(WaitUntil.Completed, name, newAccountInfo);
                storageAccount = storageAccountReq.Value;

                _logger.LogInformation($"Created storage-account '{storageAccount.Data.Name}' (public access: {(_allowPublicAccess ? "enabled" : "disabled")}).");
            }
            else
            {
                var patch = new StorageAccountPatch();
                var needsPatch = false;

                // Ensure minimum TLS version is 1.2
                if (storageAccount.Data.MinimumTlsVersion == null || !storageAccount.Data.MinimumTlsVersion.Value.ToString().Equals(StorageMinimumTlsVersion.Tls1_2.ToString()))
                {
                    _logger.LogInformation($"Updating storage account '{name}' to enforce TLS 1.2...");
                    patch.MinimumTlsVersion = StorageMinimumTlsVersion.Tls1_2;
                    needsPatch = true;
                }

                var desiredAccess = _allowPublicAccess ? StoragePublicNetworkAccess.Enabled : StoragePublicNetworkAccess.Disabled;
                if (storageAccount.Data.PublicNetworkAccess == null || storageAccount.Data.PublicNetworkAccess.Value != desiredAccess)
                {
                    _logger.LogInformation($"Updating storage account '{name}' public network access to '{desiredAccess}'...");
                    patch.PublicNetworkAccess = desiredAccess;
                    needsPatch = true;
                }

                if (needsPatch)
                {
                    await storageAccount.UpdateAsync(patch);
                }

                _logger.LogInformation($"Found existing storage-account '{storageAccount.Data.Name}'.");
                await EnsureTagsOnExisting(storageAccount.Data.Tags, storageAccount.GetTagResource());
            }

            return storageAccount;
        }
    }
}
