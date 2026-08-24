using Common.Entities;
using Common.Entities.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using Tests.FakeDataGen.Generation;

namespace Tests.FakeDataGen.Copilot
{
    /// <summary>
    /// Generates fake Copilot activity data for testing purposes.
    ///
    /// Two modes, because two very different jobs are being asked of the same data:
    ///
    /// * <b>Scatter</b> (the default) spreads <c>count</c> interactions randomly over the population.
    ///   That is what importer, volume and performance testing needs.
    /// * <b>Adoption scenario</b> instead shapes the population into deliberate personas covering every
    ///   stage of the adoption funnel. Random scatter gives every licensed user near-identical usage,
    ///   which collapses the Copilot Adoption report into a single band and makes it look broken. See
    ///   <see cref="CopilotAdoptionScenarioGenerator"/>.
    /// </summary>
    public class CopilotActivityGenerator
    {
        private readonly string _connectionString;
        private readonly Random _random = new Random();
        private readonly CopilotLicenseManager _licenseManager;
        private readonly CopilotUserManager _userManager;
        private readonly CopilotChatFactory _chatFactory;
        private readonly CopilotAdoptionScenarioGenerator _scenarioGenerator;

        public CopilotActivityGenerator(string connectionString)
        {
            _connectionString = connectionString;
            _licenseManager = new CopilotLicenseManager();
            _userManager = new CopilotUserManager(_random, _licenseManager);
            _chatFactory = new CopilotChatFactory(
                _random,
                new CopilotResourceGenerator(_random),
                new CopilotEventDetailGenerator(_random));
            _scenarioGenerator = new CopilotAdoptionScenarioGenerator(_random, _chatFactory);
        }

        /// <summary>
        /// Generates fake copilot activity events
        /// </summary>
        /// <param name="count">Number of events to generate. Ignored when <paramref name="shapeAdoptionScenario"/> is set, because the persona plan decides the volume.</param>
        /// <param name="customAgentPercentage">Percentage of events that should use custom agents (0-100)</param>
        /// <param name="agentPercentage">Percentage of events that should have agents (0-100)</param>
        /// <param name="copilotLicensePercentage">Percentage of users that should have Copilot licenses (0-100)</param>
        /// <param name="userCount">Number of test users to create when the database has none (defaults to a medium-sized company)</param>
        /// <param name="daysBack">Number of days across which generated activity is spread</param>
        /// <param name="windowEndUtc">Optional shared UTC endpoint for the generated date window</param>
        /// <param name="shapeAdoptionScenario">Shape the population into adoption personas instead of scattering events at random.</param>
        public void GenerateCopilotActivity(
            int count,
            int customAgentPercentage = 10,
            int agentPercentage = 30,
            int copilotLicensePercentage = 30,
            int userCount = 250,
            int daysBack = 90,
            DateTime? windowEndUtc = null,
            bool shapeAdoptionScenario = false)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (userCount < 1) throw new ArgumentOutOfRangeException(nameof(userCount));
            if (daysBack < 1) throw new ArgumentOutOfRangeException(nameof(daysBack));

            DateTime effectiveWindowEndUtc = windowEndUtc ?? DateTime.UtcNow;

            if (shapeAdoptionScenario)
            {
                Console.WriteLine("Generating Copilot activity shaped into adoption personas...");
                Console.WriteLine($"- Spread across the last {daysBack} day(s)");
                Console.WriteLine($"- {copilotLicensePercentage}% of users will have Copilot licenses");
            }
            else
            {
                Console.WriteLine($"Generating {count} copilot activity events...");
                Console.WriteLine($"- Spread across the last {daysBack} day(s)");
                Console.WriteLine($"- {agentPercentage}% will have agents");
                Console.WriteLine($"- {customAgentPercentage}% of those will be custom agents");
                Console.WriteLine($"- {copilotLicensePercentage}% of users will have Copilot licenses");
            }

            using (var db = new AnalyticsEntitiesContext(_connectionString, true, false))
            {
                var users = EnsurePopulation(db, userCount, copilotLicensePercentage);
                var copilotOperation = EnsureCopilotOperation(db);

                if (shapeAdoptionScenario)
                {
                    var result = _scenarioGenerator.Generate(
                        db, users, copilotOperation, effectiveWindowEndUtc,
                        historyDaysAvailable: daysBack);

                    ReportScenario(result);
                    return;
                }

                GenerateScatteredActivity(
                    db, users, copilotOperation, count, agentPercentage,
                    customAgentPercentage, daysBack, effectiveWindowEndUtc);
            }
        }

        /// <summary>Loads the existing population, creating one if the database is empty.</summary>
        private List<User> EnsurePopulation(AnalyticsEntitiesContext db, int userCount, int copilotLicensePercentage)
        {
            _licenseManager.EnsureLicensesExist(db);

            // Pull up to userCount existing users so activity is spread across a realistic population
            // rather than a handful of accounts.
            var users = db.users.OrderBy(u => u.ID).Take(userCount).ToList();
            if (users.Count == 0)
            {
                Console.WriteLine($"No users found in database. Creating {userCount} test users...");
                users = _userManager.CreateTestUsers(db, userCount, copilotLicensePercentage);
            }
            else
            {
                Console.WriteLine($"Found {users.Count} existing users in database.");
            }

            return users;
        }

        private static EventOperation EnsureCopilotOperation(AnalyticsEntitiesContext db)
        {
            var copilotOperation = db.event_operations.FirstOrDefault(o => o.Name == "CopilotInteraction");
            if (copilotOperation == null)
            {
                copilotOperation = new EventOperation { Name = "CopilotInteraction" };
                db.event_operations.Add(copilotOperation);
                db.SaveChanges();
            }
            return copilotOperation;
        }

        /// <summary>The original behaviour: N interactions scattered at random over the population.</summary>
        private void GenerateScatteredActivity(
            AnalyticsEntitiesContext db,
            List<User> users,
            EventOperation copilotOperation,
            int count,
            int agentPercentage,
            int customAgentPercentage,
            int daysBack,
            DateTime windowEndUtc)
        {
            int inserted = 0;
            int withAgents = 0;
            int withCustomAgents = 0;

            for (int i = 0; i < count; i++)
            {
                var user = users[_random.Next(users.Count)];
                bool shouldHaveAgent = _random.Next(100) < agentPercentage;
                bool isCustomAgent = shouldHaveAgent && _random.Next(100) < customAgentPercentage;

                var agent = shouldHaveAgent ? _chatFactory.GetOrCreateAgent(db, isCustomAgent) : null;

                _chatFactory.Create(
                    db,
                    user,
                    copilotOperation,
                    ActivityTimestampGenerator.Next(_random, daysBack, windowEndUtc),
                    _chatFactory.RandomAppHost(),
                    agent);

                if (shouldHaveAgent)
                {
                    withAgents++;
                    if (isCustomAgent) withCustomAgents++;
                }

                inserted++;

                if (inserted % 100 == 0)
                {
                    Console.WriteLine($"Inserted {inserted}/{count} events...");
                    db.SaveChanges();
                }
            }

            db.SaveChanges();

            Console.WriteLine($"\nGeneration complete!");
            Console.WriteLine($"Total events: {inserted}");
            Console.WriteLine($"Events with agents: {withAgents} ({(withAgents * 100.0 / inserted):F1}%)");
            Console.WriteLine($"Events with custom agents: {withCustomAgents} ({(withCustomAgents * 100.0 / inserted):F1}%)");
        }

        private static void ReportScenario(CopilotAdoptionScenarioResult result)
        {
            Console.WriteLine();
            Console.WriteLine("Adoption scenario complete!");
            Console.WriteLine($"  Interactions created:       {result.InteractionsCreated:N0}");
            Console.WriteLine($"  Licensed users shaped:      {result.LicensedUsersShaped:N0}");
            Console.WriteLine($"  Unlicensed users with use:  {result.UnlicensedUsersShaped:N0}  (licence candidates)");
            Console.WriteLine($"  Seats on disabled accounts: {result.DisabledSeats:N0}  (immediate reclaim)");
            Console.WriteLine($"  Agents planted:             {result.AgentsCreated:N0}  (one per health verdict)");

            if (result.UsersByExpectedBand.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Expected adoption funnel:");
                foreach (var band in result.UsersByExpectedBand.OrderByDescending(b => b.Value))
                {
                    Console.WriteLine($"    {band.Key,-16} {band.Value,5:N0}");
                }
            }

            if (result.UsersByPersona.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Personas planted:");
                foreach (var persona in result.UsersByPersona.OrderByDescending(p => p.Value))
                {
                    Console.WriteLine($"    {persona.Key,-38} {persona.Value,5:N0}");
                }
            }
        }

        /// <summary>
        /// Creates a new database context for external use (e.g., checking database state)
        /// </summary>
        public AnalyticsEntitiesContext CreateContext()
        {
            return new AnalyticsEntitiesContext(_connectionString, true, false);
        }
    }
}
