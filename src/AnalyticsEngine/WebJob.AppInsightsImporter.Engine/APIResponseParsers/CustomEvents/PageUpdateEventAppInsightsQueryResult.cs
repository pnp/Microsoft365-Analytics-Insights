using DataUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;

namespace WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents
{
    public class PageUpdateEventAppInsightsQueryResult : BaseCustomEventAppInsightsQueryResult
    {
        public PageUpdateEventAppInsightsQueryResult() { }
        public PageUpdateEventAppInsightsQueryResult(List<object> rowColumnVals, Dictionary<int, PropertyInfo> propDic) : base(rowColumnVals, propDic)
        {
            if (string.IsNullOrEmpty(this.CustomDimensionsJson))
            {
                this.CustomProperties = new PageUpdateEventCustomProps();
            }
            else
            {
                this.CustomProperties = JsonConvert.DeserializeObject<PageUpdateEventCustomProps>(this.CustomDimensionsJson);
            }
        }

        /// <summary>
        /// Compile single update from a list of others
        /// </summary>
        public PageUpdateEventAppInsightsQueryResult(List<PageUpdateEventAppInsightsQueryResult> allUpdates)
        {
            if (allUpdates == null || allUpdates.Count == 0) { return; }

            this.Name = allUpdates.First().Name;
            this.Username = allUpdates.First().Username;
            this.CustomProperties.Url = allUpdates.First().CustomProperties.Url;

            var existingProps = new Dictionary<string, string>();
            var existingMM = new List<TaxonomoyProperty>();

            foreach (var updateToAdd in allUpdates)
            {
                if (StringUtils.GetUrlBaseAddressIfValidUrl(updateToAdd.CustomProperties?.Url) != StringUtils.GetUrlBaseAddressIfValidUrl(this.CustomProperties.Url))
                {
                    throw new InvalidOperationException("Updates to compile are for different URLs");
                }

                foreach (var prop in updateToAdd.CustomProperties.SimplePropsDic)
                {
                    if (!existingProps.ContainsKey(prop.Key))
                    {
                        existingProps.Add(prop.Key, prop.Value);
                    }
                    else
                    {
                        existingProps[prop.Key] = prop.Value;
                    }
                }

                foreach (var mm in updateToAdd.CustomProperties.TaxonomyProps)
                {
                    // Search for duplicate MM props also by name instead of ID
                    var existingTag = existingMM.Where(m => m.PropName == mm.PropName).SingleOrDefault();
                    if (existingTag == null)
                    {
                        existingMM.Add(mm);
                    }
                }

                foreach (var like in updateToAdd.CustomProperties.Likes)
                {
                    if (!this.CustomProperties.Likes.Where(l => l.SharePointId == like.SharePointId).Any())
                    {
                        this.CustomProperties.Likes.Add(like);
                    }
                }
                foreach (var comment in updateToAdd.CustomProperties.PageComments)
                {
                    if (!this.CustomProperties.PageComments.Where(l => l.SharePointId == comment.SharePointId).Any())
                    {
                        this.CustomProperties.PageComments.Add(comment);
                    }
                }
            }



            this.CustomProperties.SetPropsDic(existingProps);
            this.CustomProperties.SetTaxProps(existingMM);

        }

        [JsonIgnore]
        public PageUpdateEventCustomProps CustomProperties { get; set; } = new PageUpdateEventCustomProps();


        public class TaxonomoyProperty
        {
            [JsonProperty("id")]
            public Guid Id { get; set; } = Guid.Empty;

            [JsonProperty("label")]
            public string Label { get; set; } = null;

            [JsonProperty("propName")]
            public string PropName { get; set; } = null;
            public bool IsValid => !string.IsNullOrEmpty(Label) && !string.IsNullOrEmpty(PropName) && Id != Guid.Empty;

            public override string ToString()
            {
                return $"{nameof(PropName)}={PropName}; {nameof(Label)}={Label}";
            }
        }

        public class UserBasedCustomAIEvent
        {
            [JsonProperty("id")]
            public int? SharePointId { get; set; }

            [JsonProperty("email")]
            public string Email { get; set; }

            [JsonProperty("creationDate")]
            public DateTime? Created { get; set; }

            public override string ToString()
            {
                return $"{this.GetType().Name}: by {this.Email}";
            }
        }

        public class PageCommentEvent : UserBasedCustomAIEvent
        {
            [JsonProperty("comment")]
            public string Comment { get; set; }

            [JsonProperty("parentId")]
            public int? ParentSharePointId { get; set; }
        }

        [JsonIgnore]
        public override bool IsValid => !string.IsNullOrEmpty(this.CustomProperties?.Url);

        public override string ToString()
        {
            return $"{this.GetType().Name}: {this.CustomProperties?.Url}";
        }
    }

    public static class PageCommentEventExtensions
    {
        public static List<TextAnalysisSample<PageCommentEvent>> ToTextAnalysisSampleList(this IEnumerable<PageCommentEvent> pageComments)
        {
            return pageComments.Where(c => c.SharePointId.HasValue)
                .Select(c => new TextAnalysisSample<PageCommentEvent> { Text = c.Comment, Id = c.SharePointId.Value.ToString(), Parent = c })
                .ToList();
        }
    }
}
