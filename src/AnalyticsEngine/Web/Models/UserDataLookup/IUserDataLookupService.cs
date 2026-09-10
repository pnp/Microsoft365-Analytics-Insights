using Common.Entities;
using System;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserDataLookup
{
    /// <summary>
    /// How a lookup finished, so the controller can pick an HTTP status without the service knowing
    /// anything about HTTP.
    /// </summary>
    public enum UserDataLookupStatus
    {
        /// <summary>The lookup succeeded; <see cref="UserDataLookupResult{T}.Value"/> is populated.</summary>
        Ok,

        /// <summary>The request was malformed (missing UPN, unknown category, ...) - HTTP 400.</summary>
        BadRequest,

        /// <summary>No user exists with the requested UPN - HTTP 404.</summary>
        UserNotFound
    }

    /// <summary>A lookup outcome: either a value, or a status plus the message to show the admin.</summary>
    public class UserDataLookupResult<T> where T : class
    {
        private UserDataLookupResult(UserDataLookupStatus status, T value, string errorMessage)
        {
            Status = status;
            Value = value;
            ErrorMessage = errorMessage;
        }

        public UserDataLookupStatus Status { get; }

        /// <summary>The payload; null unless <see cref="Status"/> is <see cref="UserDataLookupStatus.Ok"/>.</summary>
        public T Value { get; }

        /// <summary>The admin-facing message; null when <see cref="Status"/> is <see cref="UserDataLookupStatus.Ok"/>.</summary>
        public string ErrorMessage { get; }

        public static UserDataLookupResult<T> Ok(T value) => new UserDataLookupResult<T>(UserDataLookupStatus.Ok, value, null);
        public static UserDataLookupResult<T> BadRequest(string message) => new UserDataLookupResult<T>(UserDataLookupStatus.BadRequest, null, message);
        public static UserDataLookupResult<T> UserNotFound(string message) => new UserDataLookupResult<T>(UserDataLookupStatus.UserNotFound, null, message);
    }

    /// <summary>
    /// The admin user-data lookup: validation, category mapping and response shaping, with storage
    /// behind <see cref="IUserDataLookupQuery"/>. The controller keeps only model binding and the HTTP
    /// result. See issues #379 / #381.
    /// </summary>
    public interface IUserDataLookupService
    {
        /// <summary>
        /// Profile + per-category record counts for a user.
        /// </summary>
        /// <param name="upn">The user principal name; trimmed, and rejected when empty.</param>
        /// <param name="importSettingsProvider">
        /// Supplies which import workloads this deployment runs, so the page can explain why a category
        /// might legitimately have 0 records. It is a provider rather than a value so configuration is
        /// only read once the user is known to exist - exactly when the controller used to read it - and
        /// so the service itself stays free of configuration reading.
        /// </param>
        Task<UserDataLookupResult<UserDataSummaryModel>> GetSummaryAsync(string upn, Func<ImportTaskSettings> importSettingsProvider);

        /// <summary>The most recent rows for one category for a user.</summary>
        Task<UserDataLookupResult<UserDataDetailResponseModel>> GetDetailAsync(string upn, string category, int take);
    }
}
