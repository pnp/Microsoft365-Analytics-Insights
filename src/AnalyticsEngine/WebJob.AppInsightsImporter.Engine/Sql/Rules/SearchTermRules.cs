using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace WebJob.AppInsightsImporter.Engine.Sql.Rules
{
    /// <summary>
    /// Pure rules for the search-term staging path - no SQL, no ADO.NET, no logging.
    /// Modelled on ActivityAPI/Loaders/AuditLogContentDispatcher, which keeps routing/filter decisions
    /// separate from I/O. See issue #369.
    /// </summary>
    public static class SearchTermRules
    {
        /// <summary>
        /// Width of the [search_term] staging parameter, which mirrors the target column.
        /// </summary>
        public const int MaxSearchTermLength = 250;

        private const string Ellipsis = "...";

        /// <summary>
        /// A search needs a term and both components of the visit's identity, within the staging widths.
        /// A missing identity cannot acquire a visit; truncating one could acquire somebody else's.
        /// A whitespace-only search term is valid, as before.
        /// </summary>
        public static bool ShouldStage(SearchEventAppInsightsQueryResult searchEvent)
        {
            return !string.IsNullOrEmpty(searchEvent?.CustomProperties?.SearchText)
                && !string.IsNullOrWhiteSpace(searchEvent.Username) && searchEvent.Username.Length <= 250
                && !string.IsNullOrWhiteSpace(searchEvent.CustomProperties.SessionId) && searchEvent.CustomProperties.SessionId.Length <= 50;
        }

        /// <summary>
        /// Truncate a search term to <see cref="MaxSearchTermLength"/> characters, preserving the
        /// original behaviour of replacing the tail with an ellipsis.
        ///
        /// Counting is by UTF-16 char, which is what both <c>string.Length</c> and the
        /// <c>nvarchar(250)</c> parameter measure - so a Greek term is limited to 250 characters, not
        /// 250 bytes. The one refinement over the original <c>Substring(0, 247)</c> is that the cut is
        /// not allowed to land between a surrogate pair (e.g. an emoji), which would otherwise store a
        /// lone surrogate: a string SQL Server accepts but which is not valid UTF-16.
        /// </summary>
        public static string Truncate(string searchText)
        {
            if (string.IsNullOrEmpty(searchText) || searchText.Length <= MaxSearchTermLength)
            {
                return searchText;
            }

            var cut = MaxSearchTermLength - Ellipsis.Length;

            // Never split a surrogate pair: if the last kept char is a high surrogate, its low partner
            // would be cut away, leaving an unpaired surrogate.
            if (char.IsHighSurrogate(searchText[cut - 1]))
            {
                cut--;
            }

            return searchText.Substring(0, cut) + Ellipsis;
        }
    }
}
