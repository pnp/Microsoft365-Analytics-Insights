using System;
using Tests.FakeDataGen.Seeding;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class DemoPowerPlatformGenerator
    {
        private readonly DemoOptions _options;
        private readonly DemoCalendar _calendar;
        private readonly IDemoSink _sink;
        private static readonly string[] Operations =
        {
            "LaunchPowerApp", "EditPowerApp", "PublishPowerApp", "SharePowerApp",
            "CreateFlow", "EditFlow", "PutPermissions", "DeletePermissions",
            "ViewReport", "BotUpdateOperation-BotPublish", "BotUpdateOperation-BotNameUpdate"
        };
        private static readonly string[] Connectors =
        {
            "shared_sharepointonline", "shared_office365", "shared_teams", "shared_sql"
        };

        public DemoPowerPlatformGenerator(DemoOptions options, DemoCalendar calendar, IDemoSink sink)
        {
            _options = options; _calendar = calendar; _sink = sink;
        }

        public void WriteDimensions()
        {
            var platform = DemoArea.PowerApps | DemoArea.PowerAutomate | DemoArea.PowerBI | DemoArea.CopilotStudio;
            if (!_options.Includes(platform)) return;
            for (int i = 0; i < Operations.Length; i++) _sink.Write(DemoTables.Operations, 100 + i, Operations[i]);
            if (_options.Includes(DemoArea.PowerApps | DemoArea.PowerAutomate | DemoArea.CopilotStudio))
                for (int i = 1; i <= 3; i++)
                    _sink.Write(DemoTables.PowerEnvironments, i, Id(2000, i),
                        "Contoso " + new[] { "Production", "Sandbox", "Development" }[i - 1]);
            if (_options.Includes(DemoArea.PowerApps))
                for (int i = 1; i <= 4; i++)
                    _sink.Write(DemoTables.PowerClients, i, new[] { "Web", "Mobile", "Teams", "Desktop" }[i - 1]);
            if (_options.Includes(DemoArea.PowerApps | DemoArea.PowerAutomate))
                for (int i = 1; i <= Connectors.Length; i++)
                    _sink.Write(DemoTables.PowerConnectors, i, Connectors[i - 1], "Microsoft", i == 4);

            for (int department = 0; department < SeedDataCatalogue.Departments.Length; department++)
            {
                string name = "Contoso " + SeedDataCatalogue.Departments[department];
                int workspace = department + 1;
                if (_options.Includes(DemoArea.PowerBI))
                {
                    _sink.Write(DemoTables.PowerWorkspaces, workspace, Id(2001, workspace), name);
                    _sink.Write(DemoTables.PowerDashboards, workspace, Id(2002, workspace),
                        name + " Overview", workspace, _options.Start);
                }
                for (int variant = 0; variant < 2; variant++)
                {
                    int id = department * 2 + variant + 1;
                    int environment = variant == 0 ? 1 : 2 + department % 2;
                    if (_options.Includes(DemoArea.PowerApps))
                    {
                        _sink.Write(DemoTables.PowerApps, id, Id(2003, id),
                            name + (variant == 0 ? " Requests" : " Approvals"), environment, _options.Start);
                        _sink.Write(DemoTables.PowerAppConnectors, id, 1);
                        _sink.Write(DemoTables.PowerAppConnectors, id, 2 + department % 3);
                    }
                    if (_options.Includes(DemoArea.PowerAutomate))
                    {
                        _sink.Write(DemoTables.PowerFlows, id, Id(2004, id),
                            name + (variant == 0 ? " Approval routing" : " Daily digest"), environment, _options.Start);
                        _sink.Write(DemoTables.PowerFlowConnectors, id, 2);
                        _sink.Write(DemoTables.PowerFlowConnectors, id, department % 2 == 0 ? 3 : 4);
                    }
                    if (_options.Includes(DemoArea.PowerBI))
                        _sink.Write(DemoTables.PowerReports, id, Id(2005, id),
                            name + (variant == 0 ? " Performance" : " Monthly detail"),
                            variant == 0 ? "PowerBIReport" : "PaginatedReport", workspace, _options.Start);
                }
            }
            if (_options.Includes(DemoArea.CopilotStudio))
            {
                string[] bots = { "Knowledge Assistant", "Sales Coach", "Expenses Bot", "Onboarding Guide" };
                for (int i = 1; i <= bots.Length; i++)
                    _sink.Write(DemoTables.StudioBots, i, Id(2006, i), "Contoso " + bots[i - 1],
                        i == 4 ? 2 : 1, _options.Start);
            }
        }

        public void WriteDay(DemoUser user, int day, DemoDay activity)
        {
            if (!activity.HasWorkloadActivity) return;
            int objectId = user.Department * 2 + 1 + user.Id % 2;
            uint sample = DemoRandom.Value(_options.Seed, user.Id, day, 2010);
            if (_options.Includes(DemoArea.PowerApps) && sample % 100 < 55)
            {
                // Most usage is launches; a smaller maker cohort edits, publishes and shares.
                int operation = user.Id % 5 == 0 && sample % 10 < 3 ? 1 + (int)(sample % 3) : 0;
                var id = Audit(user, day, 2011, operation);
                _sink.Write(DemoTables.PowerAppEvents, id, objectId, Id(2012, user.Id, day),
                    1 + user.Id % 4);
                if (operation == 3)
                    _sink.Write(DemoTables.PowerAppShares, id, objectId, Recipient(user.Id),
                        user.Id % 2 == 0 ? "CanEdit" : "CanView");
            }
            if (_options.Includes(DemoArea.PowerAutomate) && sample / 101 % 100 < 25)
            {
                // The audit importer observes flow administration, not successful flow executions.
                int operation = 4 + (int)(sample / 1009 % 4);
                var id = Audit(user, day, 2013, operation);
                _sink.Write(DemoTables.PowerFlowEvents, id, objectId, null);
                if (operation >= 6)
                    _sink.Write(DemoTables.PowerFlowShares, id, objectId, Recipient(user.Id),
                        operation == 6 ? "CanEdit" : "CanView");
            }
            if (_options.Includes(DemoArea.PowerBI) && sample / 10007 % 100 < 65)
            {
                int views = user.Cohort == DemoCohort.High ? 3 : user.Cohort == DemoCohort.Moderate ? 2 : 1;
                for (int slot = 0; slot < views; slot++)
                {
                    int department = slot == 0 ? user.Department
                        : (user.Department + slot) % SeedDataCatalogue.Departments.Length;
                    int report = department * 2 + 1 + (user.Id + slot) % 2;
                    var id = Audit(user, day, 2014, 8, slot);
                    _sink.Write(DemoTables.PowerBIEvents, id, department + 1, report,
                        slot == 0 && user.Id % 4 == 0 ? (object)(department + 1) : null);
                }
            }
            if (_options.Includes(DemoArea.CopilotStudio) && sample / 100003 % 100 < 15)
            {
                int operation = user.Id % 3 == 0 ? 10 : 9;
                var id = Audit(user, day, 2015, operation);
                _sink.Write(DemoTables.StudioEvents, id, 1 + (user.Id + day / 7) % 4);
            }
        }

        private Guid Audit(DemoUser user, int day, short salt, int operation, int slot = 0)
        {
            var id = DemoRandom.Id(_options.Seed, salt, user.Id, day, slot);
            var time = _calendar.Timestamp(user.Zone, day, DemoRandom.Value(_options.Seed, user.Id, day, salt), slot);
            _sink.Write(DemoTables.Audit, id, user.Id, 100 + operation, time);
            return id;
        }

        private int Recipient(int user) => user % _options.Users + 1;
        private string Id(short salt, int item, int day = 0) => DemoRandom.Id(_options.Seed, salt, item, day).ToString();
    }
}
