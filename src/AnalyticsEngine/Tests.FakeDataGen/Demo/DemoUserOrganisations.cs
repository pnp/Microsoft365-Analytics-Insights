using System;
using System.Collections.Generic;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class DemoUserOrganisationType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string[] Values { get; set; }
        public int UnassignedPercent { get; set; }
        public Func<DemoUser, string> SelectValue { get; set; }
    }

    internal sealed class DemoUserOrganisations
    {
        public const byte CsvUploadSourceKind = 2;

        private static readonly string[] CostCentres =
        {
            "CC-1001 Engineering", "CC-1002 Product", "CC-1003 Design", "CC-1101 Retail Sales",
            "CC-1102 Marketing", "CC-1201 Finance", "CC-1202 People", "CC-1203 Legal",
            "CC-1301 Operations", "CC-1302 Customer Success", "CC-1401 IT", "CC-1501 Research",
            "CC-1601 Executive", "CC-1701 Αθήνα Operations"
        };

        private static readonly string[] BusinessUnits =
        {
            "Contoso Engineering", "Contoso Retail", "Contoso Shared Services", "Contoso Operations",
            "Fabrikam Services", "Northwind Field Operations", "Tailspin Consumer", "Αθήνα Operations"
        };

        private static readonly string[] Regions =
        {
            "North America West", "North America East", "United Kingdom & Ireland", "EMEA North",
            "EMEA South", "Αθήνα Operations", "Latin America", "Asia Pacific", "Middle East & Africa"
        };

        public static readonly DemoUserOrganisationType[] Types =
        {
            new DemoUserOrganisationType
            {
                Id = 1, Name = "Cost centre", Values = CostCentres, UnassignedPercent = 8,
                SelectValue = user => user.Profile.Country == "Greece"
                    ? "CC-1701 Αθήνα Operations"
                    : CostCentres[Math.Max(0, Math.Min(CostCentres.Length - 2, user.Department))]
            },
            new DemoUserOrganisationType
            {
                Id = 2, Name = "Business unit", Values = BusinessUnits, UnassignedPercent = 11,
                SelectValue = BusinessUnitFor
            },
            new DemoUserOrganisationType
            {
                Id = 3, Name = "Sales territory", Values = Regions, UnassignedPercent = 6,
                SelectValue = RegionFor
            },
        };

        private readonly int _seed;
        private readonly Dictionary<string, int>[] _valueIds;

        public DemoUserOrganisations(int seed)
        {
            _seed = seed;
            _valueIds = new Dictionary<string, int>[Types.Length];
            int id = 1;
            for (int type = 0; type < Types.Length; type++)
            {
                _valueIds[type] = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var value in Types[type].Values)
                    _valueIds[type].Add(value, id++);
            }
        }

        public int ValueCount => TotalValueCount();

        public void WriteDefinitions(IDemoSink sink, DateTime refreshedUtc)
        {
            foreach (var type in Types)
            {
                sink.Write(DemoTables.UserOrgTypes, type.Id, type.Name, CsvUploadSourceKind, null, true, 1, refreshedUtc);
                foreach (var value in type.Values)
                    sink.Write(DemoTables.UserOrgValues, _valueIds[type.Id - 1][value], type.Id, value);
            }
        }

        public int WriteAssignments(IDemoSink sink, DemoUser user, DateTime updatedUtc, IDictionary<string, int> coverage)
        {
            int written = 0;
            foreach (var type in Types)
            {
                if (IsUnassigned(user, type)) continue;
                string value = type.SelectValue(user);
                int valueId = _valueIds[type.Id - 1][value];
                sink.Write(DemoTables.UserOrgAssignments, user.Id, type.Id, valueId, updatedUtc);
                written++;
                if (coverage != null)
                    coverage[type.Name] = coverage.TryGetValue(type.Name, out var count) ? count + 1 : 1;
            }
            return written;
        }

        private bool IsUnassigned(DemoUser user, DemoUserOrganisationType type) =>
            DemoRandom.Value(_seed, user.Id, type.Id, 2400 + type.Id) % 100 < type.UnassignedPercent;

        private static string BusinessUnitFor(DemoUser user)
        {
            switch (user.Domain)
            {
                case "fabrikam.example": return "Fabrikam Services";
                case "northwind.example": return "Northwind Field Operations";
                case "tailspintoys.example": return "Tailspin Consumer";
            }

            if (user.Profile.Country == "Greece") return "Αθήνα Operations";
            switch (user.Profile.Department)
            {
                case "Sales":
                case "Marketing":
                    return "Contoso Retail";
                case "Finance":
                case "Human Resources":
                case "Legal":
                case "Executive":
                    return "Contoso Shared Services";
                case "Operations":
                case "Customer Support":
                    return "Contoso Operations";
                default:
                    return "Contoso Engineering";
            }
        }

        private static string RegionFor(DemoUser user)
        {
            switch (user.Profile.Country)
            {
                case "United States":
                    return user.Profile.StateOrProvince == "Washington" || user.Profile.StateOrProvince == "California"
                        ? "North America West" : "North America East";
                case "Canada":
                case "Mexico":
                    return "North America East";
                case "United Kingdom":
                case "Ireland":
                    return "United Kingdom & Ireland";
                case "Germany":
                case "Netherlands":
                case "Switzerland":
                case "Sweden":
                case "Poland":
                    return "EMEA North";
                case "France":
                case "Spain":
                case "Italy":
                    return "EMEA South";
                case "Greece":
                    return "Αθήνα Operations";
                case "Brazil":
                    return "Latin America";
                case "India":
                case "Japan":
                case "Australia":
                case "Singapore":
                    return "Asia Pacific";
                default:
                    return "Middle East & Africa";
            }
        }

        private int TotalValueCount()
        {
            int total = 0;
            foreach (var values in _valueIds) total += values.Count;
            return total;
        }
    }
}
