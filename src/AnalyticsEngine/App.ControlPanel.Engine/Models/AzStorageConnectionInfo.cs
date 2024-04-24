using Azure.ResourceManager.Storage;
using System.Linq;

namespace App.ControlPanel.Engine.Models
{
    /// <summary>
    /// Parses the storage account resource to get the connection string and account key
    /// </summary>
    public class AzStorageConnectionInfo
    {
        public AzStorageConnectionInfo(StorageAccountResource storage)
        {
            var keys = storage.GetKeys();

            AccountKey = keys.First().Value;
            StorageConnectionString =
                $"DefaultEndpointsProtocol=https;AccountName={storage.Data.Name};AccountKey={AccountKey};EndpointSuffix=core.windows.net";
        }

        public string AccountKey { get; set; }
        public string StorageConnectionString { get; set; }
    }
}
