using App.ControlPanel.Engine.InstallerTasks.Adoptify.Models;
using CloudInstallEngine;
using Common.DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Adoptify
{
    /// <summary>
    /// Inserts SharePoint files/images. List-items done in ListItemsInstallTask
    /// </summary>
    public class AssetsInstallTask : BaseInstallTask
    {
        private readonly ClientContext _clientContext;

        public AssetsInstallTask(TaskConfig config, ILogger logger, ClientContext clientContext) : base(config, logger)
        {
            _clientContext = clientContext;
        }

        public override async Task<object> ExecuteTask(object contextArg)
        {
            base.EnsureContextArgType<AdoptifySiteListInfo>(contextArg);
            var siteInfo = (AdoptifySiteListInfo)contextArg;

            var list = _clientContext.Web.GetListById(siteInfo.AssetsListId);
            _clientContext.Load(_clientContext.Web, w => w.ServerRelativeUrl, w => w.Url);
            await _clientContext.ExecuteQueryAsync();

            _logger.LogInformation($"Installing default Adoptify quest, level, and badge assets");

            var listBaseUrl = list.RootFolder.ServerRelativeUrl.TrimStringFromStart(_clientContext.Web.ServerRelativeUrl);

            var filesUploaded = 0;
            var rr = new ResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            using (var archive = new ZipArchive(rr.GetAssemblyManifest(ResourceNameConstants.SPAssets), ZipArchiveMode.Read))
            {
                foreach (var item in archive.Entries)
                {
                    // Is this a dir?
                    if (!string.IsNullOrEmpty(item.Name))
                    {
                        using (var fs = item.Open())
                        {
                            // We have to read into memory as CSOM doesn't support uploading via streams apparently
                            using (var ms = new MemoryStream())
                            {
                                await fs.CopyToAsync(ms);

                                var folderName = GetFolderName(item);
                                var fileCreationInfo = new FileCreationInformation
                                {
                                    Content = ms.ToArray(),
                                    Overwrite = true,
                                    Url = $"{item.Name}"
                                };

                                var targetFolderName = $"{listBaseUrl}/{folderName}";
                                var targetFolder = _clientContext.Web.EnsureFolderPath($"{targetFolderName}");

                                var uploadFile = targetFolder.Files.Add(fileCreationInfo);
                                _clientContext.Load(uploadFile);
                                filesUploaded++;
                                await _clientContext.ExecuteQueryAsync();
                            }

                        }
                    }

                }
            }
            await _clientContext.ExecuteQueryAsync();
            _logger.LogInformation($"Uploaded {filesUploaded} assets to SharePoint site");
            var resourcesBaseUrl = $"{_clientContext.Web.Url}/{listBaseUrl}";
            return new AdoptifySiteListInfoWithAssetsUrl(siteInfo) { AssetsBaseUrl = resourcesBaseUrl };
        }

        private string GetFolderName(ZipArchiveEntry item)
        {
            var url = item.FullName.TrimEnd(item.Name.ToCharArray()).TrimEnd(@"/".ToCharArray());
            return url;
        }
    }
}
