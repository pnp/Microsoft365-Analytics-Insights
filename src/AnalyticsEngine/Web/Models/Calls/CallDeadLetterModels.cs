using Common.Entities.Calls;
using Newtonsoft.Json;
using System;
using System.Net;

namespace Web.AnalyticsWeb.Models.Calls
{
    /// <summary>
    /// <c>GET api/CallDeadLetters</c>: what the Service configuration page's dead-lettered calls card shows. Facts and stable
    /// codes only - the page writes every sentence, in the reader's language.
    /// </summary>
    public sealed class CallDeadLetterStatus
    {
        /// <summary>Whether a request can reach the importer at all; when false, <see cref="UnavailableReason"/> says why.</summary>
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>One of <see cref="CallDeadLetterUnavailableReasons"/>, or null when available.</summary>
        [JsonProperty("unavailableReason")]
        public string UnavailableReason { get; set; }

        /// <summary>
        /// How many messages the Teams calls queue has dead-lettered, as Service Bus counts them (the Health page's figure);
        /// null when there is no queue or it couldn't be read (<see cref="CountUnavailable"/>).
        /// </summary>
        [JsonProperty("deadLetterCount")]
        public long? DeadLetterCount { get; set; }

        /// <summary>How many messages wait on the queue itself - including any a request just sent back.</summary>
        [JsonProperty("activeCount")]
        public long? ActiveCount { get; set; }

        /// <summary>The queue exists but its counts couldn't be read.</summary>
        [JsonProperty("countUnavailable")]
        public bool CountUnavailable { get; set; }

        /// <summary>The latest request and its result; null when none has been made.</summary>
        [JsonProperty("latest")]
        public CallDeadLetterRequestView Latest { get; set; }

        /// <summary>The runtime state table couldn't be read, so whether a request is pending is unknown.</summary>
        [JsonProperty("latestUnavailable")]
        public bool LatestUnavailable { get; set; }

        /// <summary>Whether a new request would be accepted now.</summary>
        [JsonProperty("canRequest")]
        public bool CanRequest { get; set; }

        /// <summary><see cref="CallDeadLetterRequestRules.PickUpWithin"/>, in minutes.</summary>
        [JsonProperty("pickUpWithinMinutes")]
        public int PickUpWithinMinutes { get; set; }

        /// <summary><see cref="CallDeadLetterRequestRules.FinishWithin"/>, in minutes.</summary>
        [JsonProperty("finishWithinMinutes")]
        public int FinishWithinMinutes { get; set; }

        /// <summary><see cref="CallDeadLetterRequestRules.RunBudget"/>, in minutes.</summary>
        [JsonProperty("runBudgetMinutes")]
        public int RunBudgetMinutes { get; set; }
    }

    /// <summary>A <see cref="CallDeadLetterRequest"/> with where it stands now.</summary>
    public sealed class CallDeadLetterRequestView
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary><see cref="CallDeadLetterActions.Reprocess"/> or <see cref="CallDeadLetterActions.Clear"/>.</summary>
        [JsonProperty("action")]
        public string Action { get; set; }

        /// <summary>One of <see cref="CallDeadLetterRequestStates"/>.</summary>
        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("requestedUtc")]
        public DateTime RequestedUtc { get; set; }

        [JsonProperty("claimedUtc")]
        public DateTime? ClaimedUtc { get; set; }

        [JsonProperty("finishedUtc")]
        public DateTime? FinishedUtc { get; set; }

        /// <summary>One of <see cref="CallDeadLetterOutcomes"/> once finished.</summary>
        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        /// <summary>One of <see cref="CallDeadLetterFailureCodes"/> when the outcome is not a success.</summary>
        [JsonProperty("failureCode")]
        public string FailureCode { get; set; }

        [JsonProperty("result")]
        public CallDeadLetterRunResult Result { get; set; }

        internal static CallDeadLetterRequestView From(CallDeadLetterRequest request, DateTime nowUtc)
        {
            if (request == null) return null;
            return new CallDeadLetterRequestView
            {
                Id = request.Id,
                Action = request.Action,
                State = CallDeadLetterRequestRules.StateOf(request, nowUtc),
                RequestedUtc = request.RequestedUtc,
                ClaimedUtc = request.ClaimedUtc,
                FinishedUtc = request.FinishedUtc,
                Outcome = request.Outcome,
                FailureCode = request.FailureCode,
                Result = request.Result
            };
        }
    }

    /// <summary>Body of <c>POST api/CallDeadLetters/requests</c>.</summary>
    public sealed class CallDeadLetterSubmitRequest
    {
        /// <summary><see cref="CallDeadLetterActions.Reprocess"/> or <see cref="CallDeadLetterActions.Clear"/>.</summary>
        [JsonProperty("action")]
        public string Action { get; set; }
    }

    /// <summary>A refused request: a stable code and no text.</summary>
    public sealed class CallDeadLetterError
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    /// <summary>The codes <c>api/CallDeadLetters</c> answers a refusal with, besides <see cref="CallDeadLetterUnavailableReasons"/>.</summary>
    public static class CallDeadLetterErrorCodes
    {
        /// <summary>400: the action is neither reprocess nor clear.</summary>
        public const string UnknownAction = "unknownAction";

        /// <summary>409: another request is waiting or running, and isn't stale yet.</summary>
        public const string RequestPending = "requestPending";

        /// <summary>503: the runtime state table couldn't be read or written.</summary>
        public const string StateStoreUnavailable = "stateStoreUnavailable";
    }

    /// <summary>A request the service refuses, with the HTTP status and stable code to answer it with.</summary>
    public sealed class CallDeadLetterRequestException : Exception
    {
        public CallDeadLetterRequestException(HttpStatusCode status, string code) : base(code)
        {
            Status = status;
            Code = code;
        }

        public HttpStatusCode Status { get; }

        public string Code { get; }
    }
}
