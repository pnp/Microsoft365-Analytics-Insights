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

        // varchar(1700) NOT NULL is the on-disk schema after migration ShrinkUrlsFullUrlColumn
        // (PR #108). The matching EF mapping below is what makes EF emit varchar parameters
        // instead of nvarchar, avoiding the implicit conversion that would otherwise defeat
        // IX_urls_full_url for any EF-driven query on urls. See issue #109.
        [Required]
        [MaxLength(1700)]
        [Column("full_url", TypeName = "varchar")]
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
