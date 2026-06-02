using DataUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents.PageUpdateEventAppInsightsQueryResult;

namespace WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents
{
    public abstract class BaseCustomProps
    {
        [JsonProperty("timeStamp")]
        public DateTime? EventTimestamp { get; set; }

        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("pageRequestId")]
        public Guid? PageRequestId { get; set; } = Guid.Empty;
    }

    public class PageExitCustomProps : BaseCustomProps
    {
        [JsonProperty("activeTime")]
        public double ActiveTime { get; set; }
    }

    public class ClickCustomProps : BaseCustomProps
    {
        [JsonProperty("linkText")]
        public string LinkText { get; set; }
        [JsonProperty("altText")]
        public string AltText { get; set; }
        [JsonProperty("href")]
        public string HRef { get; set; }

        [JsonProperty("classNames")]
        public string ClassNames { get; set; }
    }

    public class SearchCustomProps : BaseCustomProps
    {
        [JsonProperty("userSearch")]
        public string SearchText { get; set; }
    }

    public class PageViewCustomProps : BaseCustomProps
    {

        [JsonProperty("spRequestDuration")]
        public int SPRequestDuration { get; set; }

        [JsonProperty("pageLoad")]
        public string PageLoad { get; set; }

        [JsonProperty("webTitle")]
        public string WebTitle { get; set; }

        [JsonProperty("pageTitle")]
        public string PageTitle { get; set; }

        [JsonProperty("siteUrl")]
        public string SiteUrl { get; set; }

        [JsonProperty("webUrl")]
        public string WebUrl { get; set; }
    }

    public class PageUpdateEventCustomProps
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        /// <summary>
        /// Stringified extra props as given by SharePoint. App Insights only allows flat json custom props, so sub-objects are stringified.
        /// Newtonsoft seems to unescape string props though, which is nice...
        /// </summary>
        [JsonProperty("props")]
        public string PropsString { get; set; }


        [JsonProperty("pageComments")]
        public string CommentsString { get; set; }


        [JsonProperty("pageLikes")]
        public string LikesString { get; set; }


        /// <summary>
        /// Also Stringified MM props as given by SharePoint. App Insights only allows flat json custom props, so sub-objects are stringified.
        /// ex: { taxonomyProps: "[]"}
        /// So we have the string value and deserialise that. It's a pain but it's how it is for now.
        /// </summary>
        [JsonProperty("taxonomyProps")]
        public string TaxonomyPropsString { get; set; }

        private List<TaxonomoyProperty> _taxProps = null;
        [JsonIgnore]
        public List<TaxonomoyProperty> TaxonomyProps
        {
            get
            {
                if (_taxProps == null)
                {
                    _taxProps = TryDeserializeList<TaxonomoyProperty>(this.TaxonomyPropsString, nameof(TaxonomyPropsString));
                }
                return _taxProps;
            }
        }

        private List<PageCommentEvent> _pageComments = null;
        [JsonIgnore]
        public List<PageCommentEvent> PageComments
        {
            get
            {
                if (_pageComments == null)
                {
                    _pageComments = TryDeserializeList<PageCommentEvent>(this.CommentsString, nameof(CommentsString));
                }
                return _pageComments;
            }
        }

        private List<UserBasedCustomAIEvent> _likes = null;
        [JsonIgnore]
        public List<UserBasedCustomAIEvent> Likes
        {
            get
            {
                if (_likes == null)
                {
                    _likes = TryDeserializeList<UserBasedCustomAIEvent>(this.LikesString, nameof(LikesString));
                }
                return _likes;
            }
        }

        /// <summary>
        /// Attempt to deserialize <paramref name="json"/> as a <see cref="List{T}"/>; on parse failure or empty input
        /// returns an empty list and writes a diagnostic note. Replaces three near-identical lazy-parse blocks
        /// that each duplicated try/catch + Console.WriteLine fallback logic.
        /// </summary>
        private List<T> TryDeserializeList<T>(string json, string fieldName)
        {
            if (string.IsNullOrEmpty(json)) return new List<T>();
            try
            {
                var parsed = JsonConvert.DeserializeObject<List<T>>(json);
                return parsed ?? new List<T>();
            }
            catch (JsonException)
            {
                // Parse-error visibility: no logger is injected into the DTO so we still fall back
                // to stderr until callers thread one through. See audit follow-up TEST-09 in PR notes.
                Console.WriteLine($"\nERROR:Unexpected {fieldName} value for URL {Url}: '{json}'");
                return new List<T>();
            }
        }

        /// <summary>
        /// Get list of props in this page update. Doesn't include taxonomy fields. 
        /// Hack: it also populates comments & likes 
        /// </summary>
        private Dictionary<string, string> _propsDic = null;
        [JsonIgnore]
        public Dictionary<string, string> SimplePropsDic
        {
            get
            {
                if (_propsDic == null)
                {
                    _propsDic = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(this.PropsString))
                    {
                        var obj = StringUtils.JsonDecodeFromPropValueString(this.PropsString);
                        if (obj != null)
                        {
                            foreach (var item in obj.Children())
                            {
                                if (item.Type == JTokenType.Property)
                                {
                                    var prop = (JProperty)item;
                                    var c = item.Children().ToList();
                                    if (c.Count == 1 && (c[0].Type != JTokenType.Property && c[0].Type != JTokenType.Object))
                                    {
                                        // Only add if it's a simple value
                                        if (c[0].GetType() == typeof(JValue))
                                        {
                                            var val = (JValue)c[0];
                                            _propsDic.Add(prop.Name, val.Value?.ToString() ?? string.Empty);
                                        }
                                        else
                                        {
                                            // Look for specific properties that we know are objects and serialise them. 
                                            // Deprecated now as comments/likes are handled separately
                                            // For now just warn anyway
                                            if (prop.Name == "Comments")
                                            {
                                                Console.WriteLine($"DEBUG ERROR: Page update for '{this.Url}' contains legacy comments data. Check AITracker is updated");
                                            }
                                            else if (prop.Name == "PageLikes")
                                            {
                                                Console.WriteLine($"DEBUG ERROR: Page update for '{this.Url}' contains legacy likes data. Check AITracker is updated");
                                            }
                                            else
                                            {
                                                Console.WriteLine($"DEBUG ERROR: Page update for '{this.Url}' contains invalid JSon");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine($"DEBUG ERROR: Page update for '{this.Url}' contains invalid JSon");
                        }
                    }
                }
                return _propsDic;
            }
        }

        internal void SetPropsDic(Dictionary<string, string> existing)
        {
            _propsDic = existing;
        }
        internal void SetTaxProps(List<TaxonomoyProperty> existing)
        {
            _taxProps = existing;
        }
    }
}
