using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Which Microsoft Graph extensibility mechanism an org attribute comes from.
    /// </summary>
    /// <remarks>
    /// Open extensions are deliberately absent. They are only retrievable through
    /// <c>$expand=extensions</c>, and <c>$expand</c> is explicitly unsupported on
    /// <c>/users/delta</c>, so this importer can never read them.
    /// </remarks>
    public enum EntraOrgAttributeKind
    {
        /// <summary><c>onPremisesExtensionAttributes.extensionAttribute1..15</c>.</summary>
        OnPremisesExtensionAttribute,

        /// <summary>A built-in user property this product does not already import (e.g. <c>employeeType</c>).</summary>
        BuiltInProperty,

        /// <summary><c>employeeOrgData.costCenter</c> / <c>employeeOrgData.division</c>.</summary>
        EmployeeOrgData,

        /// <summary>A directory extension property, <c>extension_{appId}_{name}</c>.</summary>
        DirectoryExtension,

        /// <summary>A schema extension sub-property, <c>{owner}_{schemaName}.{property}</c>.</summary>
        SchemaExtension,
    }

    /// <summary>
    /// One admin-configured Entra attribute, parsed into the two things the importer actually needs:
    /// the property to ask Graph for in <c>$select</c>, and the path to dig the value out of the JSON
    /// that comes back.
    ///
    /// Pure: no Graph call, no database, no configuration. Parsing and validation can therefore be
    /// asserted directly, which matters because a bad attribute name is not a cosmetic problem -
    /// Graph rejects an unknown <c>$select</c> property with a 400 that fails the <b>entire</b>
    /// <c>/users/delta</c> request, taking the whole user import down with it.
    /// </summary>
    public sealed class EntraOrgAttributeSpec
    {
        /// <summary>Longest value Entra will hold in an <c>extensionAttributeN</c> slot.</summary>
        public const int OnPremisesExtensionAttributeMaxLength = 1024;

        /// <summary>
        /// Longest value a <c>String</c>-typed directory extension will hold. Notably shorter than an
        /// <c>extensionAttributeN</c>, which is why it is surfaced to the admin UI.
        /// </summary>
        public const int DirectoryExtensionMaxLength = 256;

        /// <summary>
        /// Built-in user properties offered as org sources.
        /// </summary>
        /// <remarks>
        /// Deliberately a short list of properties that (a) this product does not already import as
        /// its own dimension, and (b) are not in Graph's documented "stored outside the main data
        /// store" set, which is not change-trackable and so would never arrive through
        /// <c>/users/delta</c> at all. Anything already imported - department, job title, company
        /// name, office location, country, state, usage location - is excluded on purpose: it is
        /// already available as a dimension, so re-importing it as an org would only duplicate it.
        /// </remarks>
        public static readonly IReadOnlyList<string> BuiltInPropertyNames = new[]
        {
            "employeeId",
            "employeeType",
        };

        /// <summary>The two <c>employeeOrgData</c> sub-properties Graph defines.</summary>
        public static readonly IReadOnlyList<string> EmployeeOrgDataProperties = new[]
        {
            "costCenter",
            "division",
        };

        internal const string OnPremisesContainer = "onPremisesExtensionAttributes";
        internal const string EmployeeOrgDataContainer = "employeeOrgData";

        // extension_{32 hex - the owning application's id with its hyphens removed}_{name}
        private static readonly Regex DirectoryExtensionPattern =
            new Regex(@"^extension_[0-9a-fA-F]{32}_[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

        private static readonly Regex OnPremisesAttributePattern =
            new Regex(@"^extensionAttribute([0-9]{1,2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // {verifiedDomain}_{schemaName} or ext{8 random chars}_{schemaName}
        private static readonly Regex SchemaExtensionContainerPattern =
            new Regex(@"^[A-Za-z][A-Za-z0-9]*_[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private static readonly Regex SchemaExtensionPropertyPattern =
            new Regex(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

        private EntraOrgAttributeSpec(
            string canonical,
            EntraOrgAttributeKind kind,
            string selectFragment,
            IReadOnlyList<string> jsonPath,
            int? sourceMaxLength)
        {
            Canonical = canonical;
            Kind = kind;
            SelectFragment = selectFragment;
            JsonPath = jsonPath;
            SourceMaxLength = sourceMaxLength;
        }

        /// <summary>
        /// The normalised form of the attribute name, as stored in
        /// <c>user_org_types.entra_attribute_name</c>. Parsing is forgiving about casing and about the
        /// optional <c>onPremisesExtensionAttributes.</c> prefix; this is the single spelling those all
        /// collapse to, so the same slot cannot be configured twice under two different-looking names.
        /// </summary>
        public string Canonical { get; }

        /// <summary>Which extensibility mechanism this is.</summary>
        public EntraOrgAttributeKind Kind { get; }

        /// <summary>
        /// The property to add to <c>$select</c>. Always a <b>root</b> property, never a sub-path,
        /// because selecting the parent and reading the child is the pattern Microsoft documents and
        /// shows worked examples for. Several org types commonly share one fragment: all fifteen
        /// <c>extensionAttributeN</c> slots live under a single <c>onPremisesExtensionAttributes</c>.
        /// </summary>
        public string SelectFragment { get; }

        /// <summary>
        /// Where the value sits in the returned JSON, as path segments. The first segment is always
        /// <see cref="SelectFragment"/>.
        /// </summary>
        public IReadOnlyList<string> JsonPath { get; }

        /// <summary>
        /// The longest value Entra itself will store for this kind of attribute, where Microsoft
        /// documents one. Informational - the admin UI uses it to warn that the source may truncate a
        /// value before this product ever sees it. <c>null</c> when undocumented.
        /// </summary>
        public int? SourceMaxLength { get; }

        /// <summary>
        /// Parses an admin-supplied attribute name.
        /// </summary>
        /// <param name="value">The raw name typed or chosen in the portal.</param>
        /// <param name="spec">The parsed spec, or <c>null</c> when parsing failed.</param>
        /// <param name="error">
        /// An operator-facing explanation when parsing failed, or <c>null</c> on success. Worded for an
        /// IT admin, because it is shown directly in the portal.
        /// </param>
        public static bool TryParse(string value, out EntraOrgAttributeSpec spec, out string error)
        {
            spec = null;
            error = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                error = "An Entra attribute name is required.";
                return false;
            }

            var trimmed = value.Trim();

            if (trimmed.IndexOf('/') >= 0)
            {
                error =
                    "Open extensions (read with $expand=extensions) can't be used. Microsoft Graph does not " +
                    "support $expand on /users/delta, which is how this product tracks user changes. Use a " +
                    "directory extension, a schema extension, or one of the extensionAttribute1-15 slots instead.";
                return false;
            }

            if (trimmed.Any(char.IsWhiteSpace))
            {
                error = $"'{trimmed}' is not a valid attribute name - attribute names cannot contain spaces.";
                return false;
            }

            // Directory extensions are the only supported form that legitimately contains underscores
            // but no dot, so test for them before the dotted forms.
            if (trimmed.StartsWith("extension_", StringComparison.OrdinalIgnoreCase) && trimmed.IndexOf('.') < 0)
            {
                if (!DirectoryExtensionPattern.IsMatch(trimmed))
                {
                    error =
                        $"'{trimmed}' looks like a directory extension but is not in the required format " +
                        "extension_{applicationId-without-hyphens}_{name}, where the application id is exactly " +
                        "32 hexadecimal characters.";
                    return false;
                }

                spec = new EntraOrgAttributeSpec(
                    trimmed,
                    EntraOrgAttributeKind.DirectoryExtension,
                    trimmed,
                    new[] { trimmed },
                    DirectoryExtensionMaxLength);
                return true;
            }

            var dotIndex = trimmed.IndexOf('.');
            if (dotIndex < 0)
            {
                return TryParseUndotted(trimmed, out spec, out error);
            }

            if (trimmed.IndexOf('.', dotIndex + 1) >= 0)
            {
                error = $"'{trimmed}' is not a valid attribute name - it has more than one '.' separator.";
                return false;
            }

            var container = trimmed.Substring(0, dotIndex);
            var child = trimmed.Substring(dotIndex + 1);

            if (child.Length == 0)
            {
                error = $"'{trimmed}' is not a valid attribute name - nothing follows the '.' separator.";
                return false;
            }

            if (string.Equals(container, OnPremisesContainer, StringComparison.OrdinalIgnoreCase))
            {
                // Accept the fully-qualified spelling and collapse it to the short one.
                return TryParseOnPremisesAttribute(child, out spec, out error);
            }

            if (string.Equals(container, EmployeeOrgDataContainer, StringComparison.OrdinalIgnoreCase))
            {
                var known = EmployeeOrgDataProperties
                    .FirstOrDefault(p => string.Equals(p, child, StringComparison.OrdinalIgnoreCase));
                if (known == null)
                {
                    error =
                        $"'{child}' is not a property of employeeOrgData. Microsoft Graph defines only " +
                        $"{string.Join(" and ", EmployeeOrgDataProperties)}.";
                    return false;
                }

                spec = new EntraOrgAttributeSpec(
                    EmployeeOrgDataContainer + "." + known,
                    EntraOrgAttributeKind.EmployeeOrgData,
                    EmployeeOrgDataContainer,
                    new[] { EmployeeOrgDataContainer, known },
                    null);
                return true;
            }

            if (container.StartsWith("extension_", StringComparison.OrdinalIgnoreCase))
            {
                error =
                    $"'{trimmed}' is not valid. A directory extension is a single flat property, so it must " +
                    "not have a '.' sub-property.";
                return false;
            }

            if (!SchemaExtensionContainerPattern.IsMatch(container))
            {
                error =
                    $"'{container}' is not a recognised property. Expected one of the extensionAttribute1-15 " +
                    "slots, employeeOrgData.costCenter, employeeOrgData.division, a directory extension " +
                    "(extension_{appId}_{name}), or a schema extension ({owner}_{schemaName}.{property}).";
                return false;
            }

            if (!SchemaExtensionPropertyPattern.IsMatch(child))
            {
                error = $"'{child}' is not a valid schema extension property name.";
                return false;
            }

            spec = new EntraOrgAttributeSpec(
                trimmed,
                EntraOrgAttributeKind.SchemaExtension,
                container,
                new[] { container, child },
                null);
            return true;
        }

        private static bool TryParseUndotted(string trimmed, out EntraOrgAttributeSpec spec, out string error)
        {
            spec = null;
            error = null;

            if (trimmed.StartsWith("extensionAttribute", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseOnPremisesAttribute(trimmed, out spec, out error);
            }

            var builtIn = BuiltInPropertyNames
                .FirstOrDefault(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase));
            if (builtIn != null)
            {
                spec = new EntraOrgAttributeSpec(
                    builtIn,
                    EntraOrgAttributeKind.BuiltInProperty,
                    builtIn,
                    new[] { builtIn },
                    null);
                return true;
            }

            if (string.Equals(trimmed, EmployeeOrgDataContainer, StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "employeeOrgData is a container, not a value. Use employeeOrgData.costCenter or " +
                    "employeeOrgData.division.";
                return false;
            }

            if (string.Equals(trimmed, OnPremisesContainer, StringComparison.OrdinalIgnoreCase))
            {
                error =
                    "onPremisesExtensionAttributes is a container, not a value. Use one of its slots, for " +
                    "example extensionAttribute1.";
                return false;
            }

            error =
                $"'{trimmed}' is not a supported org attribute. Expected one of the extensionAttribute1-15 " +
                $"slots, {string.Join(" or ", BuiltInPropertyNames)}, employeeOrgData.costCenter, " +
                "employeeOrgData.division, a directory extension (extension_{appId}_{name}), or a schema " +
                "extension ({owner}_{schemaName}.{property}).";
            return false;
        }

        private static bool TryParseOnPremisesAttribute(string candidate, out EntraOrgAttributeSpec spec, out string error)
        {
            spec = null;
            error = null;

            var match = OnPremisesAttributePattern.Match(candidate);
            if (!match.Success)
            {
                error =
                    $"'{candidate}' is not a valid on-premises extension attribute. Expected extensionAttribute1 " +
                    "through extensionAttribute15.";
                return false;
            }

            int slot;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out slot)
                || slot < 1 || slot > 15)
            {
                error =
                    $"'{candidate}' is out of range. Entra provides extensionAttribute1 through " +
                    "extensionAttribute15.";
                return false;
            }

            // Canonicalise casing and any leading zero, so 'EXTENSIONATTRIBUTE7' and 'extensionattribute07'
            // cannot be stored as two different-looking configurations of the very same slot.
            var canonicalName = "extensionAttribute" + slot.ToString(CultureInfo.InvariantCulture);

            spec = new EntraOrgAttributeSpec(
                canonicalName,
                EntraOrgAttributeKind.OnPremisesExtensionAttribute,
                OnPremisesContainer,
                new[] { OnPremisesContainer, canonicalName },
                OnPremisesExtensionAttributeMaxLength);
            return true;
        }

        /// <summary>
        /// The <c>$select</c> fragments needed to satisfy a set of org attributes: de-duplicated
        /// (fifteen <c>extensionAttributeN</c> org types still cost exactly one property) and sorted
        /// ordinally.
        /// </summary>
        /// <remarks>
        /// The ordering is load-bearing, not cosmetic. This sequence is hashed into the Graph
        /// delta-token cache key, so an unstable order would change that key - discarding a perfectly
        /// good delta token and forcing a full re-enumeration of every user in the tenant - merely
        /// because the org types came back from the database in a different order.
        /// </remarks>
        public static IReadOnlyList<string> BuildSelectFragments(IEnumerable<EntraOrgAttributeSpec> specs)
        {
            if (specs == null)
            {
                return new string[0];
            }

            return specs
                .Where(s => s != null)
                .Select(s => s.SelectFragment)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>All fifteen on-premises extension attribute slot names, for the admin UI picker.</summary>
        public static IReadOnlyList<string> OnPremisesExtensionAttributeNames()
        {
            var names = new List<string>(15);
            for (var i = 1; i <= 15; i++)
            {
                names.Add("extensionAttribute" + i.ToString(CultureInfo.InvariantCulture));
            }
            return names;
        }

        public override string ToString()
        {
            return Canonical;
        }
    }
}
