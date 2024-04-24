using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups
{

    public class LookupidParams
    {
        [JsonPropertyName("listTitle")]
        public string ListTitle { get; set; }

        [JsonPropertyName("fieldName")]
        public string FieldName { get; set; }

        [JsonPropertyName("fieldValue")]
        public string FieldValue { get; set; }

        [JsonIgnore]
        public virtual bool IsValid => !string.IsNullOrEmpty(FieldValue) && !string.IsNullOrEmpty(FieldName) && !string.IsNullOrEmpty(ListTitle);

        public override string ToString()
        {
            return $"{nameof(ListTitle)}={ListTitle}, {nameof(FieldName)}={FieldName}, {nameof(FieldValue)}={FieldValue}";
        }
    }
    public class InsertValueIfNotExistsParams : LookupidParams
    {
        // Could be int or string, so object
        [JsonPropertyName("insertValue")]
        public object InsertValue { get; set; }

        [JsonIgnore]
        public override bool IsValid => base.IsValid && InsertValue != null;

        public override string ToString()
        {
            return $"{nameof(InsertValue)}={InsertValue} if ({nameof(FieldName)}={FieldName} != {nameof(FieldValue)}={FieldValue} in '{ListTitle}')";
        }
    }

    /// <summary>
    /// Conversion params from an image stored in site-assets lib
    /// </summary>
    public class ThumbnailLookupParams
    {
        /// <summary>
        /// Where the original image binary is uploaded in site-assets. Ex: LevelImages/goldtrophy.png
        /// </summary>
        [JsonPropertyName("siteAssetRelativeFileName")]
        public string SiteAssetRelativeFileName { get; set; }

        /// <summary>
        /// List name where the thumbnail column is
        /// </summary>
        [JsonPropertyName("thumbnailFieldHostListTitle")]
        public string ThumbnailFieldListTitle { get; set; }

        /// <summary>
        /// Field name of column to update
        /// </summary>
        [JsonPropertyName("thumbnailFieldName")]
        public string ThumbnailFieldName { get; set; }

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrEmpty(SiteAssetRelativeFileName) && !string.IsNullOrEmpty(ThumbnailFieldListTitle) && !string.IsNullOrEmpty(ThumbnailFieldName);

        public override string ToString()
        {
            return $"{nameof(SiteAssetRelativeFileName)}={SiteAssetRelativeFileName}, {nameof(ThumbnailFieldListTitle)}={ThumbnailFieldListTitle}, {nameof(ThumbnailFieldName)}={ThumbnailFieldName}";
        }
    }

    public class JsonObjectToStringParams
    {
        [JsonPropertyName("jsonPayLoad")]
        public JsonObject PayLoad { get; set; }

        [JsonIgnore]
        public bool IsValid => PayLoad != null;

    }

    /// <summary>
    /// Contents of a SharePoint image field
    /// </summary>
    public class ThumbnailFieldMetadata
    {
        [JsonPropertyName("type")]
        public string Type => "thumbnail";

        [JsonPropertyName("fileName")]
        public string FileName { get; set; }

        [JsonPropertyName("fieldName")]
        public string FieldName { get; set; }

        [JsonPropertyName("serverUrl")]
        public string ServerUrl { get; set; }

        [JsonPropertyName("fieldId")]
        public Guid FieldId { get; set; }

        [JsonPropertyName("serverRelativeUrl")]
        public string ServerRelativeUrl { get; set; }

        [JsonPropertyName("id")]
        public Guid UniqueId { get; set; }
    }
}
