using System;

namespace Common.Entities.Installer
{
    /// <summary>
    /// A Microsoft Entra principal that should be able to query the analytics database directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because Azure permits exactly ONE Microsoft Entra administrator per SQL server, and that
    /// administrator is the installer's own service principal - it is the identity that signs in to apply
    /// schema upgrades. Reassigning the administrator to a named person is therefore not a way to hand out
    /// data access: it silently evicts the installer and breaks the next upgrade with
    /// <c>Login failed for user '&lt;token-identified principal&gt;'</c>.
    /// </para>
    /// <para>
    /// The supported way is a contained database user per person, which leaves the administrator
    /// assignment alone. Nobody can create one by hand on an Entra-only server - that too requires signing
    /// in as the administrator, which is a service principal - so the installer creates them, because it is
    /// the one thing that CAN authenticate as that principal.
    /// </para>
    /// </remarks>
    public class SqlDatabaseUser
    {
        /// <summary>
        /// UPN, group name or display name of the principal. Also used verbatim as the contained database
        /// user's name, so it is what shows up in <c>sys.database_principals</c>.
        /// </summary>
        public string Login { get; set; } = string.Empty;

        /// <summary>
        /// Entra object (principal) ID. Optional: when blank the installer resolves it from
        /// <see cref="Login"/> via Microsoft Graph.
        /// </summary>
        /// <remarks>
        /// Supplying it explicitly is the escape hatch for a tenant whose app registrations have no
        /// directory-read permission - the object ID is on the principal's overview blade in the Azure
        /// portal, and with it no Graph call is needed at all.
        /// </remarks>
        public string ObjectId { get; set; } = string.Empty;

        /// <summary>"User" or "Group". Decides which Graph collection <see cref="Login"/> is resolved against.</summary>
        public string PrincipalType { get; set; } = PrincipalTypeUser;

        public const string PrincipalTypeUser = "User";
        public const string PrincipalTypeGroup = "Group";

        /// <summary>Whether this entry carries an explicit, usable object ID.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool HasObjectId
        {
            get
            {
                Guid ignored;
                return TryGetObjectId(out ignored);
            }
        }

        /// <summary>Whether <see cref="ObjectId"/> parses as a real (non-empty) GUID.</summary>
        public bool TryGetObjectId(out Guid objectId)
        {
            objectId = Guid.Empty;
            if (string.IsNullOrWhiteSpace(ObjectId)) return false;
            return Guid.TryParse(ObjectId.Trim(), out objectId) && objectId != Guid.Empty;
        }

        /// <summary>Whether this entry identifies a group rather than an individual.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool IsGroup => string.Equals((PrincipalType ?? string.Empty).Trim(), PrincipalTypeGroup, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Why this entry cannot be used, or null when it is usable. An entry needs a login - it becomes the
        /// database user name - and either an object ID or a login Graph can look up.
        /// </summary>
        public string GetValidationError()
        {
            if (!string.IsNullOrWhiteSpace(ObjectId) && !HasObjectId)
            {
                return $"'{ObjectId}' is not a valid Microsoft Entra object ID for database user '{Login}'.";
            }

            if (string.IsNullOrWhiteSpace(Login))
            {
                return "A database user needs a login (UPN or group name) to use as the database user name.";
            }

            return null;
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Login) ? (ObjectId ?? string.Empty) : Login;
        }
    }
}
