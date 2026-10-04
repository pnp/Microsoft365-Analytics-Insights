using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserFilters
{
    /// <summary>One global-filter condition as it applies to one viewer.</summary>
    public sealed class ResolvedGlobalFilterClause
    {
        internal ResolvedGlobalFilterClause(GlobalFilterClause source, string viewerValue, bool unresolved, UserFilterClause effective)
        {
            Source = source;
            ViewerValue = viewerValue;
            Unresolved = unresolved;
            Effective = effective;
        }

        /// <summary>The condition as the administrator wrote it.</summary>
        public GlobalFilterClause Source { get; }

        /// <summary>The viewer's own value the condition used, when it uses one and the viewer has it.</summary>
        public string ViewerValue { get; }

        /// <summary>
        /// True when the condition needs a value from the viewer that the directory does not have for
        /// them - they are not in it, or hold no value for the attribute. Such a condition matches nobody.
        /// </summary>
        public bool Unresolved { get; }

        /// <summary>The condition with every value fixed, as it is evaluated.</summary>
        public UserFilterClause Effective { get; }
    }

    /// <summary>The global filter resolved for one viewer: every condition with its values fixed.</summary>
    public sealed class ResolvedGlobalFilter
    {
        internal ResolvedGlobalFilter(GlobalFilterDefinition definition, bool viewerFound, IReadOnlyList<ResolvedGlobalFilterClause> clauses)
        {
            Definition = definition;
            ViewerFound = viewerFound;
            Clauses = clauses;
            Expression = new UserFilterExpression(clauses.Select(c => c.Effective));
            Key = UserFilterCodec.Serialize(Expression) ?? string.Empty;
        }

        public GlobalFilterDefinition Definition { get; }

        /// <summary>Whether the directory holds the person viewing - which only matters when a condition uses their values.</summary>
        public bool ViewerFound { get; }

        public IReadOnlyList<ResolvedGlobalFilterClause> Clauses { get; }

        /// <summary>The filter as it is evaluated for this viewer. Never empty for a non-empty definition.</summary>
        public UserFilterExpression Expression { get; }

        /// <summary>True when any condition needed a value the viewer does not have, and so matches nobody.</summary>
        public bool AnyUnresolved => Clauses.Any(c => c.Unresolved);

        /// <summary>
        /// The resolved filter in its wire form: identical for two viewers the filter treats identically -
        /// two people in the same department, say - so they can share cached results.
        /// </summary>
        public string Key { get; }
    }

    /// <summary>
    /// Resolves the administrator's global filter for the person viewing a report.
    /// </summary>
    /// <remarks>
    /// <para><b>Fails closed.</b> A condition that needs the viewer's own value and cannot get it - the viewer
    /// is not in the directory, or has no department - matches NOBODY, whatever its operator. "Department
    /// is not the viewer's department" with no department to compare would otherwise match everyone, and
    /// a filter an administrator set to restrict what people see must never widen because the directory is
    /// missing a value. An empty report is the failure a reader notices and asks about; a widened one is
    /// not. The page says why (see <see cref="ResolvedGlobalFilterClause.Unresolved"/>).</para>
    /// <para>Conditions with fixed values only are taken as written, so a global filter with no viewer
    /// conditions resolves the same for everyone.</para>
    /// </remarks>
    public static class GlobalFilterResolver
    {
        /// <param name="viewerRow">The viewer's row in <paramref name="snapshot"/>, or <c>null</c> when it does not hold them.</param>
        public static ResolvedGlobalFilter Resolve(GlobalFilterDefinition definition, UserDirectorySnapshot snapshot, int? viewerRow)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var resolved = new List<ResolvedGlobalFilterClause>(definition.Clauses.Count);
            foreach (var source in definition.Clauses)
            {
                resolved.Add(ResolveClause(source, snapshot, viewerRow));
            }

            return new ResolvedGlobalFilter(definition, viewerRow.HasValue, resolved.AsReadOnly());
        }

        private static ResolvedGlobalFilterClause ResolveClause(GlobalFilterClause source, UserDirectorySnapshot snapshot, int? viewerRow)
        {
            var clause = source.Clause;
            if (!source.UsesViewer) return new ResolvedGlobalFilterClause(source, null, false, clause);

            var viewerValue = viewerRow.HasValue ? snapshot.ValueOf(source.ViewerAttribute, viewerRow.Value) : null;
            if (string.IsNullOrWhiteSpace(viewerValue))
            {
                // "Is" with nothing to compare with and "not set" excluded: matches nobody - see the remarks.
                var nobody = new UserFilterClause(clause.Join, clause.Dimension, UserFilterOperator.Is, Enumerable.Empty<string>(), includeNotSet: false);
                return new ResolvedGlobalFilterClause(source, null, true, nobody);
            }

            var values = new List<string>(clause.Values);
            if (!values.Contains(viewerValue, StringComparer.OrdinalIgnoreCase)) values.Add(viewerValue);

            var effective = new UserFilterClause(clause.Join, clause.Dimension, clause.Operator, values, clause.IncludeNotSet);
            return new ResolvedGlobalFilterClause(source, viewerValue, false, effective);
        }
    }
}
