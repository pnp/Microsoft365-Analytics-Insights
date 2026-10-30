using System;

namespace Common.Entities.CopilotCosts
{
    public sealed class CostApiHealth
    {
        public DateTimeOffset? LastAttempt { get; set; }
        public DateTimeOffset? LastSuccessfulRead { get; set; }
        public DateTimeOffset? FailureSince { get; set; }
        public string FailureCode { get; set; }

        public void RecordSuccess(DateTimeOffset now)
        {
            LastSuccessfulRead = now;
            FailureSince = null;
            FailureCode = null;
        }

        public void RecordFailure(string code, DateTimeOffset now)
        {
            FailureSince = FailureSince ?? now;
            FailureCode = SafeCode(code);
        }

        public static string SafeCode(string code)
        {
            switch (code)
            {
                case "reconnectRequired":
                case "permissionDenied":
                case "identityUnavailable":
                case "throttled":
                case "sourceUnavailable":
                case "invalidResponse":
                case "invalidPage":
                case "storageUnavailable":
                case "storageCorrupt":
                case "databaseUnavailable":
                case "accountMismatch":
                    return code;
                default:
                    return "sourceFailure";
            }
        }
    }
}
