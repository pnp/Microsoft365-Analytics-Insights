using Common.Entities.UserOrgs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// The extra Graph properties this tenant's configured user-org types need, and the qualifier that
    /// ties a stored delta token to them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both facts have to come from one object, because getting them out of step is silently
    /// destructive. Graph freezes <c>$select</c> when a delta token is minted, so a token created
    /// without an org attribute keeps returning responses without it no matter what the next request
    /// asks for. If the token key did not move with the selection, adding an org type would appear to
    /// work and then quietly never populate for any user who did not otherwise change - which on an
    /// established tenant is almost everybody.
    /// </para>
    /// <para>
    /// <see cref="DeltaKeyQualifier"/> is empty when no Entra org types are configured, so a deployment
    /// that never uses this feature keeps the exact delta-token cache key it has today and does not pay
    /// for a full re-enumeration on upgrade. The qualifier only appears once an admin opts in.
    /// </para>
    /// </remarks>
    public sealed class GraphUserOrgSelection
    {
        /// <summary>No org attributes configured - the selection and the token key are unchanged.</summary>
        public static readonly GraphUserOrgSelection None =
            new GraphUserOrgSelection(new string[0], new string[0]);

        private GraphUserOrgSelection(IReadOnlyList<string> selectFragments, IReadOnlyList<string> unparseable)
        {
            SelectFragments = selectFragments;
            UnparseableAttributeNames = unparseable;
            DeltaKeyQualifier = BuildQualifier(selectFragments);
        }

        /// <summary>
        /// The root Graph properties to add to <c>$select</c>: ordered, de-duplicated, and never
        /// containing a sub-path.
        /// </summary>
        public IReadOnlyList<string> SelectFragments { get; }

        /// <summary>
        /// Stored attribute names that no longer parse. Reported rather than thrown, so one bad row of
        /// configuration cannot stop the user import.
        /// </summary>
        public IReadOnlyList<string> UnparseableAttributeNames { get; }

        /// <summary>
        /// Appended to the delta-token cache key. Empty string when nothing is configured.
        /// </summary>
        public string DeltaKeyQualifier { get; }

        public bool IsEmpty
        {
            get { return SelectFragments.Count == 0; }
        }

        /// <summary>
        /// Builds a selection from the <c>entra_attribute_name</c> values of the enabled Entra org types.
        /// </summary>
        public static GraphUserOrgSelection FromAttributeNames(IEnumerable<string> attributeNames)
        {
            IReadOnlyList<string> unparseable;
            var specs = UserOrgRules.ParseSpecs(attributeNames, out unparseable);
            var fragments = EntraOrgAttributeSpec.BuildSelectFragments(specs);

            if (fragments.Count == 0 && unparseable.Count == 0)
            {
                return None;
            }

            return new GraphUserOrgSelection(fragments, unparseable);
        }

        /// <summary>
        /// Builds a selection from already-parsed specs.
        /// </summary>
        public static GraphUserOrgSelection FromSpecs(IEnumerable<EntraOrgAttributeSpec> specs)
        {
            var fragments = EntraOrgAttributeSpec.BuildSelectFragments(specs);
            return fragments.Count == 0 ? None : new GraphUserOrgSelection(fragments, new string[0]);
        }

        /// <summary>
        /// Adds the org properties to a base <c>$select</c> list, skipping any the base already names.
        /// </summary>
        /// <remarks>
        /// Graph rejects a duplicated property in <c>$select</c>, and the base list already contains
        /// several user properties, so this has to be a union rather than a concatenation.
        /// </remarks>
        public string BuildSelect(string baseSelect)
        {
            if (IsEmpty)
            {
                return baseSelect;
            }

            var existing = new HashSet<string>(
                (baseSelect ?? string.Empty).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            var additions = SelectFragments.Where(f => !existing.Contains(f)).ToList();
            if (additions.Count == 0)
            {
                return baseSelect;
            }

            return string.IsNullOrEmpty(baseSelect)
                ? string.Join(",", additions)
                : baseSelect + "," + string.Join(",", additions);
        }

        /// <summary>
        /// A short, stable hash of the selected properties.
        /// </summary>
        /// <remarks>
        /// A hash rather than the property names themselves because a directory extension name is 60-odd
        /// characters and several of them would make an unwieldy cache key. Truncated to 8 bytes: this
        /// is a cache-invalidation tag, not a security boundary, and a collision would only mean reusing
        /// a delta token for a different selection - which a full re-enumeration on the next
        /// configuration change would correct anyway.
        ///
        /// SHA-256 rather than <see cref="string.GetHashCode()"/>, which is randomised per process on
        /// modern .NET and would therefore invalidate the token on every web-job restart.
        /// </remarks>
        private static string BuildQualifier(IReadOnlyList<string> fragments)
        {
            if (fragments == null || fragments.Count == 0)
            {
                return string.Empty;
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(",", fragments)));
                var builder = new StringBuilder(18);
                builder.Append("-o");
                for (var i = 0; i < 8; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        public override string ToString()
        {
            return IsEmpty ? "(no org attributes)" : string.Join(",", SelectFragments);
        }
    }
}
