using Microsoft.SharePoint.Client;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.SharePointModelBuilder
{
    /// <summary>
    /// Filename parsing for name + extension
    /// </summary>
    public class SPFileInfo
    {
        public SPFileInfo(string fileName)
        {
            var i = fileName.LastIndexOf('.');
            if (i == -1)
            {
                this.Extension = string.Empty;
                this.FileNameNoExtension = fileName;
            }
            else
            {
                this.FileNameNoExtension = fileName.Substring(0, i);
                const int DOT_LEN = 1;

                var extStart = FileNameNoExtension.Length + DOT_LEN;
                this.Extension = fileName.Substring(extStart, fileName.Length - extStart);
            }
        }

        public string FileNameNoExtension { get; set; }
        public string Extension { get; set; }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Extension))
            {
                return $"{FileNameNoExtension}";
            }
            else
            {
                return $"{FileNameNoExtension}.{Extension}";
            }
        }
    }

    public static class FileUtilsExtensions
    {
        public static async Task<string> GetFileNameThatDoesntExistYet(this ClientContext clientContext, string rootPath, string initialName)
        {
            if (rootPath.EndsWith("/"))
            {
                rootPath = rootPath.Substring(0, rootPath.Length - 1);
            }

            // Ensure it exists in SiteAssets/{listId} - 
            var nameIsUnique = false;
            var fileTitle = initialName;
            var fullFileName = string.Empty;
            var i = 1;
            while (!nameIsUnique)
            {
                fullFileName = $"{rootPath}/{fileTitle}";
                var fileExists = await FileExistsByServerRelativeUrl(clientContext.Web, fullFileName);
                nameIsUnique = !fileExists;

                if (fileExists)
                {
                    var newFileName = new SPFileInfo(initialName);
                    newFileName.FileNameNoExtension += i;
                    fileTitle = newFileName.ToString();
                }
                i++;
            }

            return fullFileName;
        }

        public static async Task<bool> FileExistsByServerRelativeUrl(this Web web, string serverRelativeUrl)
        {
            var ctx = web.Context;
            File file;
            try
            {
                file = web.GetFileByServerRelativeUrl(serverRelativeUrl);
                ctx.Load(file);
                await ctx.ExecuteQueryAsync();
                return true;
            }
            catch (ServerException ex)
            {
                if (ex.ServerErrorTypeName == "System.IO.FileNotFoundException")
                {
                    file = null;
                    return false;
                }
                else
                    throw;
            }
        }
    }
}
