using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{

    /// <summary>
    /// A set of standard list properties for a file
    /// </summary>
    [Table("file_metadata_property_values")]
    public class FileMetadataPropertyValue : AbstractEFEntity
    {
        [ForeignKey(nameof(Url))]
        [Column("url_id")]
        public int UrlId { get; set; }

        public Url Url { get; set; }

        [ForeignKey(nameof(Field))]
        [Column("field_id")]
        public int FieldId { get; set; }

        public FileMetadataFieldName Field { get; set; }

        [Column("field_value")]
        public string FieldValue { get; set; }

        /// <summary>
        /// If property is a taxonomy value, this is the tag ID
        /// </summary>
        [Column("tag_guid")]
        public Guid? TagGuid { get; set; }

        [Column("updated")]
        public DateTime Updated { get; set; }

        public override string ToString()
        {
            return $"{Field?.Name}: {FieldValue}";
        }
    }


    /// <summary>
    /// URL metadata fieldname definitions
    /// </summary>
    [Table("file_field_definitions")]
    public class FileMetadataFieldName : AbstractEFEntityWithName
    {
    }
}
