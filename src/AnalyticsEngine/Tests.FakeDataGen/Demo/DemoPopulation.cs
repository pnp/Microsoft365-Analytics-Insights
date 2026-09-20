using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Copilot;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Demo
{
    internal enum DemoCohort { High, Moderate, Low, Zero, Inactive }

    internal sealed class DemoSku
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string PartNumber { get; set; }
        public int Members { get; set; }
        public int Offset { get; set; }
        public int Stride { get; set; }
        public int RangeStart { get; set; }

        public bool Includes(int userId, int population) =>
            ((long)(userId - 1) * Stride + Offset) % population >= RangeStart
            && ((long)(userId - 1) * Stride + Offset) % population < RangeStart + Members;
    }

    internal sealed class DemoUser
    {
        public int Id { get; set; }

        /// <summary>
        /// The email domain this person's sign-in name sits on - in a tenant assembled by acquisition,
        /// effectively which company they work for. Assigned by <see cref="DemoPopulation.DomainFor"/>.
        /// </summary>
        public string Domain { get; set; } = DemoPopulation.PrimaryDomain;

        public string Upn => "demo.user" + Id.ToString("D7", System.Globalization.CultureInfo.InvariantCulture) + "@" + Domain;
        public SeedDataCatalogue.UserProfile Profile { get; set; }
        public string Zone { get; set; }
        public DemoCohort Cohort { get; set; }
        public bool CopilotLicensed { get; set; }
        public bool UnlicensedDemand { get; set; }
        public AdoptionPersona Persona { get; set; }
        public int Department { get; set; }
    }

    internal sealed class DemoPopulation
    {
        private readonly DemoOptions _options;
        public IReadOnlyList<DemoSku> Skus { get; }

        /// <summary>The parent organisation's domain - the one most of the tenant sits on.</summary>
        public const string PrimaryDomain = "contoso.example";

        /// <summary>
        /// The email domains the demo tenant is spread across, parent first.
        ///
        /// <para>A tenant carrying several verified domains is the normal shape after an acquisition or
        /// a rebrand, and it is the case the Copilot Adoption email-domain view exists to serve: a
        /// department spans every company in the tenant, so a departmental average hides exactly the
        /// difference between the parent and a business that was bought and never onboarded.</para>
        ///
        /// <para>Before this the whole demo population sat on a single domain, which meant the one
        /// dataset anyone would reach for to look at, demo or screenshot that view was the one dataset
        /// that could never show it - both the domain table and the domain filter hide themselves
        /// below two domains, correctly.</para>
        ///
        /// <para>Reserved <c>.example</c> names throughout, per the repository's synthetic-data rule.</para>
        /// </summary>
        public static readonly string[] Domains =
        {
            PrimaryDomain,          // the parent
            "fabrikam.example",     // a large, reasonably well-integrated acquisition
            "northwind.example",    // a smaller one, integrated later
            "tailspintoys.example", // the most recent, barely onboarded
        };

        /// <summary>
        /// Per-cohort domain weights, in <see cref="Domains"/> order, each row summing to 100.
        ///
        /// <para>Domain is drawn conditionally on the cohort rather than independently of it. That is
        /// what makes the demo tell the story the view is for - the acquired businesses skew towards
        /// the low and idle cohorts, so the table shows a parent that is doing well next to a business
        /// nobody onboarded - while leaving each cohort's total size exactly as
        /// <see cref="DemoOptions.Mix"/> specified it. Drawing the domain independently would have kept
        /// the mix too, but every domain would then show the same adoption rate and the table would be
        /// a row of identical numbers.</para>
        /// </summary>
        private static readonly int[][] DomainWeightsByCohort =
        {
            new[] { 88, 7, 3, 2 },   // High
            new[] { 84, 10, 4, 2 },  // Moderate
            new[] { 66, 18, 10, 6 }, // Low
            new[] { 58, 21, 12, 9 }, // Zero
            new[] { 55, 22, 13, 10 },// Inactive
        };

        /// <summary>
        /// Deterministic domain for a user, given the cohort they landed in. Stable across reruns for a
        /// given seed, like every other demo attribute, so a regenerated database is comparable with
        /// the one before it.
        /// </summary>
        public static string DomainFor(int seed, int id, DemoCohort cohort)
        {
            var weights = DomainWeightsByCohort[(int)cohort];
            int bucket = (int)(DemoRandom.Value(seed, id, 0, 2300) % 100);

            int cumulative = 0;
            for (int i = 0; i < weights.Length - 1; i++)
            {
                cumulative += weights[i];
                if (bucket < cumulative) return Domains[i];
            }

            return Domains[weights.Length - 1];
        }

        /// <summary>
        /// The Entra object id written to <c>dbo.users.azure_ad_id</c>. Shared rather than restated because
        /// the agent-cost data joins to a user on exactly this value, and a second copy of the expression
        /// would be a silent way for the two to drift apart.
        /// </summary>
        internal static string AzureAdObjectId(int seed, int userId) => DemoRandom.Id(seed, 1, userId).ToString();

        public DemoPopulation(DemoOptions options)
        {
            _options = options;
            var core = SeedDataCatalogue.LicenseCatalogue;
            var names = new[]
            {
                ("Contoso Demo Workplace", "CONTOSO_DEMO_WORKPLACE"),
                core[0], core[2], core[1], core[4], core[6], core[9], core[10], core[7], core[8]
            };
            int[] rare = { 1, 5, 25, 50, 100 };
            var skus = new List<DemoSku>();
            for (int id = 1; id <= options.Skus; id++)
            {
                var sku = new DemoSku
                {
                    Id = id,
                    Name = id <= names.Length ? names[id - 1].Item1 : $"Contoso Demo Add-on {id:D3}",
                    PartNumber = id <= names.Length ? names[id - 1].Item2 : $"CONTOSO_DEMO_ADDON_{id:D3}",
                    Stride = CoprimeStride(options.Users, (int)(DemoRandom.Value(options.Seed, id, 0, 1) % 997) + 1),
                    Offset = (int)(DemoRandom.Value(options.Seed, id, 0, 2) % (uint)options.Users)
                };
                if (id == 1) sku.Members = options.Users;
                else if (id == 2) sku.Members = options.Users * options.CopilotPercent / 100;
                else if (id >= 6 && id <= 10) sku.Members = Math.Min(options.Users, rare[id - 6]);
                else if (id >= 11) sku.Members = (int)((long)options.Users * (10 + id * 17 % 71) / 100);
                skus.Add(sku);
            }
            // E3/E5/Business are alternatives; overlapping memberships come from add-ons.
            int e3 = options.Users * 60 / 100, e5 = options.Users * 25 / 100;
            for (int i = 2; i <= 4; i++)
            {
                skus[i].Stride = skus[2].Stride;
                skus[i].Offset = skus[2].Offset;
                skus[i].RangeStart = i == 2 ? 0 : i == 3 ? e3 : e3 + e5;
                skus[i].Members = i == 2 ? e3 : i == 3 ? e5 : options.Users - e3 - e5;
            }
            Skus = skus;
        }

        public DemoUser User(int id)
        {
            var profile = SeedDataCatalogue.NextUserProfile(new Random((int)(DemoRandom.Value(_options.Seed, id, 0, 3) & int.MaxValue)));
            int bucket = (int)(DemoRandom.Value(_options.Seed, id, 0, 4) % 100);
            int cumulative = 0, cohort = 0;
            for (; cohort < _options.Mix.Length - 1; cohort++)
            {
                cumulative += _options.Mix[cohort];
                if (bucket < cumulative) break;
            }
            var user = new DemoUser
            {
                Id = id, Profile = profile, Cohort = (DemoCohort)cohort,
                CopilotLicensed = Skus[1].Includes(id, _options.Users),
                Department = Array.IndexOf(SeedDataCatalogue.Departments, profile.Department)
            };
            user.Domain = DomainFor(_options.Seed, id, user.Cohort);
            profile.AccountEnabled = user.Cohort != DemoCohort.Inactive || id % 3 == 0;
            user.Zone = DemoCalendar.ZoneFor(profile);
            user.UnlicensedDemand = !user.CopilotLicensed && profile.AccountEnabled
                && user.Cohort <= DemoCohort.Low && DemoRandom.Value(_options.Seed, id, 0, 5) % 100 < 18;
            if (user.Cohort == DemoCohort.Zero) user.Persona = CopilotAdoptionPersonas.NeverUsed;
            else if (user.Cohort == DemoCohort.Inactive) user.Persona = CopilotAdoptionPersonas.DormantRecentlyLapsed;
            else if (user.Cohort == DemoCohort.Low)
                user.Persona = id % 3 == 0 ? CopilotAdoptionPersonas.DevelopingBroadOccasional
                    : id % 3 == 1 ? CopilotAdoptionPersonas.TriallingCurious : CopilotAdoptionPersonas.TriallingOneOff;
            else
            {
                user.Persona = CopilotAdoptionPersonas.Pick(
                    CopilotAdoptionPersonas.MixFor((DepartmentMaturity)(user.Department % 3)),
                    new Random((int)(DemoRandom.Value(_options.Seed, id, 0, 6) & int.MaxValue)));
                if (!user.Persona.AccountEnabled) user.Persona = CopilotAdoptionPersonas.NeverUsed;
            }
            return user;
        }

        private static int CoprimeStride(int population, int candidate)
        {
            while (Gcd(population, candidate) != 1) candidate++;
            return candidate;
        }

        private static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }
    }
}
