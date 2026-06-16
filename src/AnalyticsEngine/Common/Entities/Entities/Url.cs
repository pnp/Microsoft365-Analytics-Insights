using Common.Entities.Entities;
using DataUtils;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// A URL of a hit/file in SPO
    /// </summary>
    [Table("urls")]
    public class Url : AbstractEFEntity, IUrlObject
    {
        /// <summary>
        /// Maximum length (characters) of <see cref="FullUrl"/> / <c>dbo.urls.full_url</c>. The
        /// column is <c>nvarchar(850)</c> after migration ShrinkUrlsFullUrlColumn (850 nvarchar
        /// chars = the 1700-byte non-clustered index-key limit). Used to gate insert-time URL
        /// trimming (see <see cref="DataUtils.StringUtils.EnsureUrlWithinLength"/>) so the
        /// importer never stages a URL longer than the target column. See issue #122 (#108/#109).
        /// </summary>
        public const int FullUrlMaxLength = 850;

        // nvarchar(850) NOT NULL is the on-disk schema after migration ShrinkUrlsFullUrlColumn
        // (the column is shrunk from a (max) LOB so it can be the IX_urls_full_url index key -
        // 850 nvarchar chars = the 1700-byte index-key limit). It MUST be nvarchar (not varchar):
        // full_url holds SharePoint URLs that can contain any Unicode (e.g. Greek), which varchar
        // would corrupt to '?'. The matching EF mapping below keeps EF emitting nvarchar parameters
        // so EF-driven queries on urls can use IX_urls_full_url. See issue #122 (#108/#109).
        [Required]
        [MaxLength(FullUrlMaxLength)]
        [Column("full_url", TypeName = "nvarchar")]
        public string FullUrl { get; set; }

        [Column("file_last_refreshed")]
        public DateTime? MetadataLastRefreshed { get; set; } = null;

        public List<FileMetadataPropertyValue> UrlMetadataProps { get; set; } = new List<FileMetadataPropertyValue>();

        string IUrlObject.Url => FullUrl;

        public override string ToString()
        {
            return $"{base.ToString()},{nameof(FullUrl)}={FullUrl}";
        }
    }
}
