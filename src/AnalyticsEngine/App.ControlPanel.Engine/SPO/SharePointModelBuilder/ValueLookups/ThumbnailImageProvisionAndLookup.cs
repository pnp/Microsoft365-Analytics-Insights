using CloudInstallEngine.Models;
using Microsoft.SharePoint.Client;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups
{
    public class ThumbnailImageProvisionAndLookup : AbstractSPListItemValueLookup
    {
        public const string PROP_LOOKUP_TYPE_ID_LOOKUP = nameof(ThumbnailImageProvisionAndLookup);
        public ThumbnailImageProvisionAndLookup(ClientContext clientContext, string paramsJson, bool required) : base(clientContext, required)
        {
            this.Params = JsonSerializer.Deserialize<ThumbnailLookupParams>(paramsJson);
        }
        public ThumbnailLookupParams Params { get; set; }

        public override bool IsValid => Params != null && Params.IsValid;

        protected override async Task<string> LookupValue()
        {
            var t = await LookupThumbnailValue();
            return JsonSerializer.Serialize(t);
        }

        async Task<ListItem> GetFileFromLookupParams(List siteAssetsList)
        {
            var fileName = $"{siteAssetsList.RootFolder.ServerRelativeUrl}/{Params.SiteAssetRelativeFileName}";
            return await GetFile(siteAssetsList, fileName);
        }
        async Task<ListItem> GetFile(List siteAssetsList, string fileName)
        {
            var query = new CamlQuery();
            query.ViewXml = $"<View Scope=\"RecursiveAll\"><Query><Where><Eq><FieldRef Name='FileRef' /><Value Type='File'>{fileName}</Value></Eq></Where></Query></View>";
            var results = siteAssetsList.GetItems(query);
            _clientContext.Load(results);

            await _clientContext.ExecuteQueryAsync();
            if (results.Count == 0)
            {
                throw new LookupNoResultsException(this.Params.ToString());
            }
            else if (results.Count == 1)
            {
                return results[0];
            }
            else
            {
                throw new LookupTooManyResultsException(this.Params.ToString());
            }
        }

        protected async Task<ThumbnailFieldMetadata> LookupThumbnailValue()
        {
            // Load key resources
            var hostList = _clientContext.Web.GetListByTitle(Params.ThumbnailFieldListTitle);
            _clientContext.Load(hostList.Fields);
            _clientContext.Load(hostList, l => l.Title, l => l.Id, l => l.Fields);
            _clientContext.Load(_clientContext.Web);

            var siteAssetsList = _clientContext.Web.GetListByTitle("Site Assets");
            _clientContext.Load(siteAssetsList, l => l.RootFolder, l => l.RootFolder.ServerRelativeUrl);
            await _clientContext.ExecuteQueryAsync();

            // Find file in source library
            var originalListItem = await GetFileFromLookupParams(siteAssetsList);

            // Extract "https://m365x72460609.sharepoint.com" from web URL
            const string SP_DOMAIN = "sharepoint.com";
            var tenantUrl = _clientContext.Web.Url;
            var spDomainStart = tenantUrl.IndexOf(SP_DOMAIN);
            if (spDomainStart == -1) throw new InstallException($"Unexpected SP domain in {_clientContext.Web.ServerRelativeUrl}");
            tenantUrl = tenantUrl.Substring(0, spDomainStart + SP_DOMAIN.Length);


            var sourceListItemFileName = originalListItem["FileLeafRef"]?.ToString();
            var sourceListItemFileNameFull = originalListItem["FileRef"]?.ToString();

            // Ensure source file exists in SiteAssets/Lists/{listId}
            var listsFolder = await siteAssetsList.RootFolder.EnsureFolderAsync("Lists");
            await listsFolder.EnsureFolderAsync(hostList.Id.ToString());

            // We need to copy the file to the special SP list location for thumbnails. Find a filename that doesn't exist there 1st. 
            var siteAssetsDestListCopyFolderName = $"{siteAssetsList.RootFolder.ServerRelativeUrl}/Lists/{hostList.Id}/";
            var siteAssetsDestListCopyFileName = await _clientContext.GetFileNameThatDoesntExistYet(siteAssetsDestListCopyFolderName, sourceListItemFileName);


            // Copy to right site assets folder with new unique name
            MoveCopyUtil.CopyFileByPath(_clientContext, ResourcePath.FromDecodedUrl(tenantUrl + sourceListItemFileNameFull), ResourcePath.FromDecodedUrl(tenantUrl + siteAssetsDestListCopyFileName),
                false, new MoveCopyOptions());
            await _clientContext.ExecuteQueryAsync();

            // Load new file list-item
            var assetsCopyLI = await GetFile(siteAssetsList, siteAssetsDestListCopyFileName);

            var field = hostList.Fields.Where(f => f.StaticName == Params.ThumbnailFieldName).SingleOrDefault();
            if (field == null)
            {
                throw new InstallException($"Can't find field '{Params.ThumbnailFieldName}' on list {hostList.Title}");
            }

            var uidStr = assetsCopyLI.FieldValues["UniqueId"]?.ToString();
            var uid = Guid.Empty;
            if (!Guid.TryParse(uidStr, out uid))
            {
                throw new InstallException($"Field '{Params.ThumbnailFieldName}' on list {hostList.Title} has no unique ID field");
            }

            return new ThumbnailFieldMetadata
            {
                FieldId = field.Id,
                ServerRelativeUrl = siteAssetsDestListCopyFileName,
                FieldName = Params.ThumbnailFieldName,
                UniqueId = uid,
                FileName = Params.SiteAssetRelativeFileName,
                ServerUrl = tenantUrl
            };
        }

    }
}
