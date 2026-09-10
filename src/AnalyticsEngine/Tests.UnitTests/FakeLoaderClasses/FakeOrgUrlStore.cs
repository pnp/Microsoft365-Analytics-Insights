using App.ControlPanel.Engine.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// In-memory <see cref="IOrgUrlStore"/>. Lookups are case-insensitive to mirror the database's
    /// default <c>Latin1_General_CI_AS</c> collation, but the values themselves are kept verbatim so a
    /// test can assert exactly what would have been written to <c>url_base</c>.
    /// </summary>
    public class FakeOrgUrlStore : IOrgUrlStore
    {
        public class InsertedRow
        {
            public string UrlBase { get; set; }
            public int OrgId { get; set; }
        }

        private readonly List<string> _rows = new List<string>();

        /// <summary>Every insert this store was asked to perform, in order.</summary>
        public List<InsertedRow> Inserts { get; } = new List<InsertedRow>();

        /// <summary>How many times the caller checked for existence (guards against per-row churn).</summary>
        public int ExistsCallCount { get; private set; }

        public FakeOrgUrlStore(params string[] existingRows)
        {
            if (existingRows != null)
            {
                _rows.AddRange(existingRows);
            }
        }

        /// <summary>Everything currently "in the table", pre-existing rows included.</summary>
        public IReadOnlyList<string> Rows => _rows;

        public bool Exists(string urlBase)
        {
            ExistsCallCount++;
            return _rows.Any(r => string.Equals(r, urlBase, StringComparison.OrdinalIgnoreCase));
        }

        public void Insert(string urlBase, int orgId)
        {
            _rows.Add(urlBase);
            Inserts.Add(new InsertedRow { UrlBase = urlBase, OrgId = orgId });
        }
    }
}
