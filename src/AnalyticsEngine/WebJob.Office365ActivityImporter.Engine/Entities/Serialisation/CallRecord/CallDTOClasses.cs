using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public abstract class BaseCallRecordDTO : BaseGraphSerialisationClass
    {
        [JsonProperty("startDateTime")]
        public DateTime StartDateTime { get; set; }

        [JsonProperty("endDateTime")]
        public DateTime EndDateTime { get; set; }

        [JsonProperty("type")]
        public string CallType { get; set; }
    }

    public abstract class BaseCallRecordDTOWithModalities : BaseCallRecordDTO
    {
        [JsonProperty("modalities")]
        public string[] Modalities { get; set; }
    }

    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-session?view=graph-rest-beta
    public class CallSessionDTO : BaseCallRecordDTOWithModalities
    {
        [JsonProperty("failureInfo")]
        public FailureInfoDTO FailureInfo { get; set; }


        [JsonProperty("caller")]
        public ParticipantEndpointDTO Caller { get; set; }
        [JsonProperty("callee")]
        public ParticipantEndpointDTO Callee { get; set; }
    }

    public class CallSessionSegment : BaseCallRecordDTO
    {

    }

    public class CallSessionListDTO
    {
        [JsonProperty("value")]
        public List<CallSessionDTO> Sessions { get; set; }

        [JsonProperty("@odata.nextlink")]
        public string OdataNextlink { get; set; }

    }


    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-participantendpoint?view=graph-rest-beta
    public class ParticipantEndpointDTO
    {
        [JsonProperty("userAgent")]
        public UserAgentDTO UserAgent { get; set; }

        [JsonProperty("feedback")]
        public UserFeedbackDTO Feedback { get; set; }

        [JsonIgnore]
        public string UserEmailAddress { get; set; }

        [JsonIgnore]
        public bool HaveUserEmail { get { return !string.IsNullOrEmpty(UserEmailAddress); } }

        [JsonProperty("identity")]
        public IdentitySetDTO Identity { get; set; }
    }

    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-useragent?view=graph-rest-beta
    public class UserAgentDTO
    {
        [JsonProperty("applicationVersion")]
        public string ApplicationVersion { get; set; }
        [JsonProperty("headerValue")]
        public string HeaderValue { get; set; }
    }

    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-userfeedback?view=graph-rest-beta
    public class UserFeedbackDTO
    {
        [JsonProperty("rating")]
        public string Rating { get; set; }
        [JsonProperty("text")]
        public string Text { get; set; }
    }

    // https://docs.microsoft.com/en-us/graph/api/resources/callrecords-failureinfo?view=graph-rest-beta
    public class FailureInfoDTO
    {
        [JsonProperty("reason")]
        public string Reason { get; set; }
        [JsonProperty("stage")]
        public string Stage { get; set; }
    }
}
