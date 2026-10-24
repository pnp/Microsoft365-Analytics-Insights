using System;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    /// <summary>What reading a call's organiser identity (and resolving it to a UPN) found.</summary>
    public enum CallOrganiserStatus
    {
        /// <summary>Not worked out: the call was built by hand rather than loaded from Graph.</summary>
        NotEvaluated = 0,

        /// <summary>The organiser's UPN is known.</summary>
        Resolved,

        /// <summary>
        /// The payload has no organiser identity at all - neither <c>organizer_v2</c> nor the retired <c>organizer</c>.
        /// That is a schema problem (Graph stopped sending what we read), not a property of this call.
        /// </summary>
        NoIdentity,

        /// <summary>An organiser identity is present but it is not an Entra user (a phone number, say).</summary>
        NotAUser,

        /// <summary>The organiser is a user of another tenant.</summary>
        ExternalTenant,

        /// <summary>Entra ID has no such user (deleted, or not a user object).</summary>
        NotFoundInDirectory
    }

    /// <summary>What was found while resolving the people on one call, for its one outcome log line.</summary>
    public sealed class CallLoadStats
    {
        /// <summary>Pages of sessions read, the call record itself being the first.</summary>
        public int SessionPages { get; set; } = 1;

        /// <summary>Distinct people of this tenant on the call, the organiser included.</summary>
        public int DistinctUsers { get; set; }

        /// <summary>Distinct people from other tenants, who are never looked up.</summary>
        public int ExternalUsers { get; set; }

        /// <summary>People of this tenant Entra ID could not return a UPN for.</summary>
        public int UnresolvedUsers { get; set; }

        /// <summary>How long resolving the people took, in milliseconds.</summary>
        public long ResolveMs { get; set; }
    }

    /// <summary>
    /// The call record Graph returned cannot be used, in a way that is about its payload rather than about this
    /// attempt: it is retried (a later version of a call record can carry more) and then dead-lettered with
    /// <see cref="Reason"/>, rather than completed as though it had been processed.
    /// </summary>
    public class CallRecordPayloadException : Exception
    {
        /// <summary>The call record carries no organiser identity at all.</summary>
        public const string NoOrganiserIdentityReason = "NoOrganiserIdentity";

        public CallRecordPayloadException(string callId, string reason, string message)
            : base(message)
        {
            CallId = callId;
            Reason = reason;
        }

        public string CallId { get; }

        /// <summary>The dead-letter reason, e.g. <see cref="NoOrganiserIdentityReason"/>.</summary>
        public string Reason { get; }
    }
}
