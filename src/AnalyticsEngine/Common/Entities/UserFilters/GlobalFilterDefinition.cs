using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>
    /// One condition of the administrator's global filter: a user filter clause whose values can include
    /// the value the person viewing the report holds for an attribute - "Department is the viewer's own
    /// department" - as well as, or instead of, fixed values.
    /// </summary>
    /// <remarks>
    /// Immutable, and only built by <see cref="GlobalFilterCodec"/> (or a test), which validates it.
    /// </remarks>
    public sealed class GlobalFilterClause
    {
        public GlobalFilterClause(UserFilterClause clause, string viewerAttribute)
        {
            Clause = clause ?? throw new ArgumentNullException(nameof(clause));
            ViewerAttribute = string.IsNullOrWhiteSpace(viewerAttribute) ? null : viewerAttribute;
        }

        /// <summary>
        /// The condition with its fixed values. May hold no values at all when
        /// <see cref="ViewerAttribute"/> supplies them.
        /// </summary>
        public UserFilterClause Clause { get; }

        /// <summary>
        /// The attribute of the person viewing the report whose value is added to the condition's values,
        /// or <c>null</c> for a condition with fixed values only. A dimension key, e.g. <c>department</c>,
        /// or <c>userName</c> for the viewer themselves.
        /// </summary>
        public string ViewerAttribute { get; }

        /// <summary>True when the condition depends on who is looking.</summary>
        public bool UsesViewer => ViewerAttribute != null;
    }

    /// <summary>
    /// The administrator's global filter: conditions every insights report applies on top of whatever the
    /// reader filters by themselves, and which only a portal administrator can change.
    /// </summary>
    /// <remarks>
    /// Kept as the flat list the administrator built, joined by AND and OR exactly like a user filter
    /// (AND binds tighter). It is resolved for one viewer at a time by <see cref="GlobalFilterResolver"/>.
    /// </remarks>
    public sealed class GlobalFilterDefinition
    {
        public static readonly GlobalFilterDefinition Empty = new GlobalFilterDefinition(Enumerable.Empty<GlobalFilterClause>());

        public GlobalFilterDefinition(IEnumerable<GlobalFilterClause> clauses)
        {
            Clauses = (clauses ?? Enumerable.Empty<GlobalFilterClause>()).ToList().AsReadOnly();
        }

        public IReadOnlyList<GlobalFilterClause> Clauses { get; }

        /// <summary>True when there is no global filter: every report covers the whole tenant.</summary>
        public bool IsEmpty => Clauses.Count == 0;

        /// <summary>True when what the filter matches depends on who is viewing.</summary>
        public bool UsesViewer => Clauses.Any(c => c.UsesViewer);
    }

    /// <summary>
    /// Which of the viewer's own attributes a global-filter condition may compare with.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately a short list rather than any attribute against any other. Comparing a
    /// department with the viewer's country is never what an administrator meant, and a condition that
    /// can only ever match nobody is a mistake the editor should not offer.</para>
    /// <list type="bullet">
    ///   <item>Most attributes compare with the viewer's own value of the same attribute: "Department is
    ///   the viewer's department", "Cost centre is the viewer's cost centre".</item>
    ///   <item>The people-valued attributes - manager and management chain - compare with the viewer
    ///   themselves ("Management chain includes the viewer": their whole organisation) or with the
    ///   viewer's manager ("Manager is the viewer's manager": their peers).</item>
    ///   <item>The sign-in name compares with the viewer: "only my own figures".</item>
    /// </list>
    /// </remarks>
    public static class GlobalFilterViewerAttributes
    {
        private static readonly IReadOnlyList<string> PersonValued = new[]
        {
            UserFilterDimensions.UserName,
            UserFilterDimensions.Manager,
        };

        private static readonly IReadOnlyList<string> ViewerOnly = new[] { UserFilterDimensions.UserName };

        /// <summary>The viewer attributes a condition on <paramref name="dimension"/> may use, in the order the editor offers them.</summary>
        public static IReadOnlyList<string> AllowedFor(string dimension)
        {
            if (!UserFilterDimensions.IsWellFormed(dimension)) return new string[0];

            if (string.Equals(dimension, UserFilterDimensions.UserName, StringComparison.Ordinal)) return ViewerOnly;

            if (string.Equals(dimension, UserFilterDimensions.Manager, StringComparison.Ordinal)
                || string.Equals(dimension, UserFilterDimensions.ManagementChain, StringComparison.Ordinal))
            {
                return PersonValued;
            }

            return new[] { dimension };
        }

        public static bool IsAllowed(string dimension, string viewerAttribute)
        {
            return viewerAttribute != null && AllowedFor(dimension).Contains(viewerAttribute, StringComparer.Ordinal);
        }
    }
}
