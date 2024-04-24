using Common.DataUtils;
using Common.Entities.Entities;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// A URL of a hit/file in SPO
    /// </summary>
    [Table("urls")]
    public class Url : AbstractEFEntity, IUrlObject
    {

        [Column("full_url")]
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
