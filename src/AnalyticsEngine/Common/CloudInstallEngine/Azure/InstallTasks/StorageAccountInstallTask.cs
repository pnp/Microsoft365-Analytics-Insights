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
        public StorageAccountInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
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
                var newAccountInfo = new StorageAccountCreateOrUpdateContent(new StorageSku("Standard_LRS"), StorageKind.StorageV2, AzureLocation);
                EnsureTagsOnNew(newAccountInfo.Tags);
                var storageAccountReq = await Container.GetStorageAccounts().CreateOrUpdateAsync(WaitUntil.Completed, name, newAccountInfo);
                storageAccount = storageAccountReq.Value;

                _logger.LogInformation($"Created storage-account '{storageAccount.Data.Name}'");
            }
            else
            {
                _logger.LogInformation($"Found existing storage-account '{storageAccount.Data.Name}'.");
                await EnsureTagsOnExisting(storageAccount.Data.Tags, storageAccount.GetTagResource());
            }

            return storageAccount;
        }
    }
}
