using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.Models
{
    // https://docs.microsoft.com/en-us/graph/api/resources/resourcedata?view=graph-rest-1.0
    public class ResourceData
    {
        [JsonProperty("oDataType")]
        public string ODataType { get; set; }

        [JsonProperty("oDataId")]
        public string ODataId { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }
    }

    // https://docs.microsoft.com/en-us/graph/api/resources/changenotification?view=graph-rest-1.0
    public class GraphChangeNotification
    {
        [JsonProperty("tenantId")]
        public string TenantId { get; set; }

        [JsonProperty("subscriptionId")]
        public string SubscriptionId { get; set; }

        [JsonProperty("clientState")]
        public string ClientState { get; set; }

        [JsonProperty("changeType")]
        public string ChangeType { get; set; }

        [JsonProperty("resource")]
        public string Resource { get; set; }

        [JsonProperty("subscriptionExpirationDateTime")]
        public DateTime SubscriptionExpirationDateTime { get; set; }

        [JsonProperty("resourceData")]
        public ResourceData ResourceData { get; set; }
    }

    // https://docs.microsoft.com/en-us/graph/api/resources/changenotificationcollection?view=graph-rest-1.0
    public class GraphChangeNotificationList
    {
        [JsonProperty("value")]
        public List<GraphChangeNotification> Notifications { get; set; }
    }
}
