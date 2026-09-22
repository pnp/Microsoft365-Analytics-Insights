using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// The pure rules behind user orgs: how a raw value from Entra or a CSV becomes the value stored
    /// in <c>user_org_values.name</c>, and how a configured attribute is dug out of the JSON Graph
    /// returns.
    ///
    /// No database, no Graph call, no configuration - so every rule here can be asserted directly.
    /// </summary>
    public static class UserOrgRules
    {
        /// <summary>
        /// Maximum length of an org value. Matches <c>user_org_values.name nvarchar(200)</c>.
        /// </summary>
        /// <remarks>
        /// <c>nvarchar</c>, not <c>varchar</c>: org names come from a customer tenant and routinely
        /// contain non-Latin scripts. 200 also keeps the column comfortably inside SQL Server's
        /// 1700-byte non-clustered index key limit (850 characters at 2 bytes each), which matters
        /// because the value is indexed for "who is in this org?" lookups.
        /// </remarks>
        public const int MaxOrgValueLength = 200;

        /// <summary>Maximum length of an org type name. Matches <c>user_org_types.name nvarchar(100)</c>.</summary>
        public const int MaxOrgTypeNameLength = 100;

        /// <summary>Maximum length of a UPN we will match against. Matches <c>dbo.users.user_name</c>.</summary>
        public const int MaxUpnLength = 250;

        /// <summary>
        /// Turns a raw org value into the value that gets stored, or <c>null</c> to mean "this user has
        /// no value for this org type" - which the callers treat as an instruction to clear any
        /// existing assignment.
        /// </summary>
        /// <remarks>
        /// Trimming happens first, so a value that is only whitespace clears the assignment rather than
        /// creating a blank org. Over-length values are truncated rather than rejected: an org name
        /// longer than 200 characters is vanishingly unlikely, and silently dropping the user from the
        /// org would be a worse outcome than storing a shortened name. Truncation is reported by
        /// <see cref="WouldTruncate"/> so the admin UI and the import summary can say it happened.
        /// </remarks>
        public static string NormaliseOrgValue(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return null;
            }

            var trimmed = rawValue.Trim();
            if (trimmed.Length > MaxOrgValueLength)
            {
                trimmed = trimmed.Substring(0, MaxOrgValueLength);

                // Truncation can leave trailing whitespace that SQL Server would ignore on comparison
                // but that would still be stored; trim again so the stored value is exactly what a
                // later lookup will match.
                trimmed = trimmed.TrimEnd();
                if (trimmed.Length == 0)
                {
                    return null;
                }
            }

            return trimmed;
        }

        /// <summary>Whether <see cref="NormaliseOrgValue"/> would shorten this value.</summary>
        public static bool WouldTruncate(string rawValue)
        {
            return !string.IsNullOrWhiteSpace(rawValue) && rawValue.Trim().Length > MaxOrgValueLength;
        }

        /// <summary>
        /// Whether saving this org type must first prove its attribute is readable from Microsoft
        /// Graph.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only an <b>enabled Entra</b> type. The parse check alone is not enough for one: a
        /// well-formed name can still name a property this tenant does not have, and Graph answers
        /// that with a 400 that fails the entire <c>/users/delta</c> request - taking licences,
        /// managers and department metadata down with it until the importer's fallback notices.
        /// </para>
        /// <para>
        /// A <b>disabled</b> type is never read, so its attribute cannot break anything, and
        /// demanding a probe makes the feature's own recovery advice impossible to follow. When a
        /// directory extension is deleted from the tenant the import logs an error telling the
        /// administrator to fix the type on the admin page - and turning it off is the only fix that
        /// keeps the values, because deleting, repointing and switching to CSV all discard them. A
        /// probe would refuse precisely that.
        /// </para>
        /// </remarks>
        public static bool RequiresLiveAttributeProof(UserOrgSourceKind sourceKind, bool isEnabled)
        {
            return sourceKind == UserOrgSourceKind.EntraAttribute && isEnabled;
        }

        /// <summary>
        /// Normalises a UPN for matching against <c>dbo.users.user_name</c>, or <c>null</c> when the
        /// value is unusable.
        /// </summary>
        /// <remarks>
        /// Case is deliberately left alone. The database collation (<c>Latin1_General_CI_AS</c>) is
        /// already case-insensitive and every in-memory lookup uses
        /// <see cref="StringComparer.OrdinalIgnoreCase"/>, so lower-casing here would buy nothing and
        /// would cost a string allocation per row - 200,000 of them on a large tenant.
        /// </remarks>
        public static string NormaliseUpn(string rawUpn)
        {
            if (string.IsNullOrWhiteSpace(rawUpn))
            {
                return null;
            }

            var trimmed = rawUpn.Trim();
            return trimmed.Length > MaxUpnLength ? null : trimmed;
        }

        /// <summary>
        /// Validates an admin-supplied org type name.
        /// </summary>
        public static bool TryNormaliseOrgTypeName(string rawName, out string normalised, out string error)
        {
            normalised = null;
            error = null;

            if (string.IsNullOrWhiteSpace(rawName))
            {
                error = "An organisation type name is required.";
                return false;
            }

            var trimmed = rawName.Trim();
            if (trimmed.Length > MaxOrgTypeNameLength)
            {
                error = $"An organisation type name can be at most {MaxOrgTypeNameLength} characters.";
                return false;
            }

            normalised = trimmed;
            return true;
        }

        /// <summary>
        /// Digs the configured attribute's value out of the extra JSON properties Graph returned for a
        /// user, returning the <b>raw</b> value (un-normalised) or <c>null</c> when the user has no
        /// value for it.
        /// </summary>
        /// <remarks>
        /// A missing key and an explicit JSON <c>null</c> are treated identically, and both mean "no
        /// value". This is required, not merely convenient: Microsoft documents that a property which
        /// has never been set is omitted from a delta response entirely, while a property that has been
        /// cleared comes back as <c>null</c>, and for directory extensions the docs do not say which of
        /// the two you get. Distinguishing them would therefore be reading meaning into an
        /// implementation detail.
        ///
        /// Non-string primitives are accepted and rendered as text: a cost centre of <c>4021</c> is a
        /// perfectly ordinary org value, and refusing it because the JSON happened to be unquoted would
        /// be surprising. Objects and arrays are rejected - there is no sensible single value to take.
        /// </remarks>
        public static string ExtractRawValue(IDictionary<string, JToken> graphProperties, EntraOrgAttributeSpec spec)
        {
            if (graphProperties == null || spec == null || spec.JsonPath == null || spec.JsonPath.Count == 0)
            {
                return null;
            }

            JToken current;
            if (!TryGetProperty(graphProperties, spec.JsonPath[0], out current))
            {
                return null;
            }

            for (var i = 1; i < spec.JsonPath.Count; i++)
            {
                var container = current as JObject;
                if (container == null)
                {
                    return null;
                }

                // Graph echoes back the casing used in $select, but a case-insensitive walk costs
                // nothing here and removes a whole class of "it works in one tenant" bug.
                current = container.GetValue(spec.JsonPath[i], StringComparison.OrdinalIgnoreCase);
                if (current == null)
                {
                    return null;
                }
            }

            return TokenToValue(current);
        }

        private static bool TryGetProperty(IDictionary<string, JToken> properties, string name, out JToken value)
        {
            if (properties.TryGetValue(name, out value))
            {
                return true;
            }

            foreach (var pair in properties)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        private static string TokenToValue(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Object || token.Type == JTokenType.Array)
            {
                return null;
            }

            var value = token as JValue;
            if (value == null || value.Value == null)
            {
                return null;
            }

            if (value.Type == JTokenType.String)
            {
                return (string)value.Value;
            }

            // Invariant culture so a decimal cost centre does not acquire a comma on a machine with a
            // European locale, which would then be stored as a different org from the same value read
            // on an invariant host.
            return Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses the <c>entra_attribute_name</c> column for a set of org types, discarding (rather than
        /// throwing on) any that no longer parse.
        /// </summary>
        /// <remarks>
        /// Tolerant on purpose. These values were validated against live Graph before being saved, so a
        /// value that no longer parses means the rules have since been tightened. Throwing here would
        /// take down the entire user import - the exact failure mode this feature is built to avoid -
        /// so the bad type is skipped and reported instead.
        /// </remarks>
        public static IReadOnlyList<EntraOrgAttributeSpec> ParseSpecs(
            IEnumerable<string> attributeNames,
            out IReadOnlyList<string> unparseable)
        {
            var specs = new List<EntraOrgAttributeSpec>();
            var bad = new List<string>();

            foreach (var name in attributeNames ?? Enumerable.Empty<string>())
            {
                EntraOrgAttributeSpec spec;
                string error;
                if (EntraOrgAttributeSpec.TryParse(name, out spec, out error))
                {
                    specs.Add(spec);
                }
                else
                {
                    bad.Add(name);
                }
            }

            unparseable = bad;
            return specs;
        }

        /// <summary>
        /// Collapses a batch of assignment updates so that at most one survives per
        /// (user, org type) slot, keeping the <b>last</b> one.
        /// </summary>
        /// <param name="updates">The raw batch.</param>
        /// <param name="duplicatesCollapsed">How many updates were discarded.</param>
        /// <remarks>
        /// Necessary, not defensive. <c>user_org_assignments</c> is keyed on (user_id, org_type_id), so
        /// two rows for the same slot in one bulk batch would violate the primary key and fail the whole
        /// import. Last-wins matches what an admin expects from a CSV that lists a person twice - the
        /// later line is the correction - and the discarded count is reported so the import summary can
        /// say the file had duplicates rather than silently picking one.
        ///
        /// Ordinal comparison on the value is deliberate: this only decides whether to *report* a
        /// duplicate, and SQL Server's case-insensitive collation still decides what is ultimately
        /// stored.
        /// </remarks>
        public static IReadOnlyList<UserOrgAssignmentUpdate> DeduplicateUpdates(
            IReadOnlyList<UserOrgAssignmentUpdate> updates,
            out int duplicatesCollapsed)
        {
            duplicatesCollapsed = 0;
            if (updates == null || updates.Count == 0)
            {
                return new UserOrgAssignmentUpdate[0];
            }

            // Index of the surviving entry for each slot, so "last wins" costs one pass, not a
            // reverse-then-reverse. At 200k users x several org types the allocation difference matters.
            var slotToIndex = new Dictionary<UserOrgAssignmentUpdate, int>(
                updates.Count, UserOrgAssignmentSlotComparer.Instance);
            var kept = new List<UserOrgAssignmentUpdate>(updates.Count);

            foreach (var update in updates)
            {
                if (update == null)
                {
                    continue;
                }

                int existingIndex;
                if (slotToIndex.TryGetValue(update, out existingIndex))
                {
                    kept[existingIndex] = update;
                    duplicatesCollapsed++;
                }
                else
                {
                    slotToIndex[update] = kept.Count;
                    kept.Add(update);
                }
            }

            return kept;
        }
    }
}
