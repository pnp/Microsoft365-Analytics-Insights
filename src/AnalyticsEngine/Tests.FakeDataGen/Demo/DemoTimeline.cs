using Common.Entities.CopilotAdoption;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Demo
{
    internal sealed class DemoDay
    {
        public int Messages, Meetings, Sent, Received, Read, SharePointFiles, OneDriveFiles, EngageRead;
        public int CopilotTurns, CopilotSequence;
        public bool HasWorkloadActivity => Messages + Meetings + Sent + Read + SharePointFiles + OneDriveFiles + EngageRead > 0;
    }

    internal sealed class DemoTimeline
    {
        public static readonly string[] Hosts = { "bizchat", "Teams", "Word", "Outlook", "Excel", "PowerPoint", "OneNote", "cowork" };
        public static readonly string[] ReportApps =
            { "Any App", "Copilot Chat (work)", "Microsoft Teams", "Word", "Outlook", "Excel", "PowerPoint", "OneNote", "Copilot agents" };
        private readonly DemoOptions _options;
        private readonly DemoUser _user;
        public DemoDay[] Days { get; }

        public DemoTimeline(DemoOptions options, DemoUser user)
        {
            _options = options;
            _user = user;
            Days = Enumerable.Range(0, options.Days).Select(_ => new DemoDay()).ToArray();
            PopulateCopilot();
            for (int d = 0; d < Days.Length; d++)
            {
                if (!DemoCalendar.IsWorkingDate(options.Start.AddDays(d))) continue;
                var day = Days[d];
                int age = options.Days - d;
                if (user.Cohort == DemoCohort.Zero || (user.Cohort == DemoCohort.Inactive && age <= 60)) continue;
                var cohort = user.Cohort == DemoCohort.Inactive ? DemoCohort.Moderate : user.Cohort;
                int chance = cohort == DemoCohort.High ? 92 : cohort == DemoCohort.Moderate ? 70 : 28;
                if (IsOnLeave(user.Id, d)) continue;
                if (Random(d, 20) % 100 >= chance && day.CopilotTurns == 0) continue;
                int strength = cohort == DemoCohort.High ? 3 : cohort == DemoCohort.Moderate ? 2 : 1;
                if (Random(d, 21) % 100 < 85)
                {
                    day.Messages = strength == 3 ? 70 + (int)(Random(d, 22) % 70)
                        : strength == 2 ? 20 + (int)(Random(d, 22) % 40) : 2 + (int)(Random(d, 22) % 9);
                    day.Meetings = strength == 3 ? 3 + (int)(Random(d, 23) % 4)
                        : strength == 2 ? 1 + (int)(Random(d, 23) % 3) : (int)(Random(d, 23) % 2);
                }
                if (Random(d, 24) % 100 < 92)
                {
                    day.Sent = strength == 3 ? 30 + (int)(Random(d, 25) % 40)
                        : strength == 2 ? 10 + (int)(Random(d, 25) % 18) : 1 + (int)(Random(d, 25) % 5);
                    day.Read = day.Sent * 2 + (int)(Random(d, 26) % 12);
                    day.Received = day.Read + day.Sent;
                }
                if (Random(d, 27) % 100 < 80)
                {
                    day.SharePointFiles = strength == 3 ? 25 + (int)(Random(d, 28) % 40)
                        : strength == 2 ? 8 + (int)(Random(d, 28) % 15) : 1 + (int)(Random(d, 28) % 5);
                    day.OneDriveFiles = day.SharePointFiles / 2 + 1;
                }
                if (Random(d, 29) % 100 < 22) day.EngageRead = 2 + (int)(Random(d, 30) % 12);
            }
        }

        private uint Random(int day, int salt) => DemoRandom.Value(_options.Seed, _user.Id, day, salt);

        public static bool IsOnLeave(int userId, int day) => (day / 7) % 26 == userId % 26;

        private void PopulateCopilot()
        {
            if (!_user.CopilotLicensed && !_user.UnlicensedDemand) return;
            var persona = _user.Persona;
            if (_user.CopilotLicensed && persona.ExpectedBand == AdoptionBand.NeverUsed) return;

            int wanted = _user.CopilotLicensed ? persona.ActiveDaysInWindow : 4 + _user.Id % 5;
            int perDay = _user.CopilotLicensed ? (int)Math.Round(persona.InteractionsPerActiveDay) : 2 + _user.Id % 2;
            var recent = new List<int>();
            for (int d = Math.Max(0, Days.Length - 28); d < Days.Length; d++)
                if (DemoCalendar.IsWorkingDate(_options.Start.AddDays(d)) && !IsOnLeave(_user.Id, d)) recent.Add(d);
            recent.Sort((a, b) =>
            {
                int compare = Random(a, 31).CompareTo(Random(b, 31));
                return compare == 0 ? a.CompareTo(b) : compare;
            });
            for (int i = 0; i < Math.Min(wanted, recent.Count); i++)
                Days[recent[i]].CopilotTurns = Math.Max(1, perDay);

            int firstAge = _user.CopilotLicensed
                ? persona.ExpectedBand == AdoptionBand.Champion ? 110 + _user.Id % 60
                    : persona.ExpectedBand == AdoptionBand.Established ? 75 + _user.Id % 75
                    : persona.ExpectedBand == AdoptionBand.Developing ? 40 + _user.Id % 55
                    : persona.ExpectedBand == AdoptionBand.Dormant ? 160 : 35
                : 45 + _user.Id % 40;
            int lastAge = persona.ExpectedBand == AdoptionBand.Dormant
                ? (_user.Cohort == DemoCohort.Inactive ? 61 : 42 + _user.Id % 20) : 29;
            for (int d = 0; d < Days.Length - 28; d++)
            {
                int age = Days.Length - d;
                if (age > firstAge || age < lastAge || !DemoCalendar.IsWorkingDate(_options.Start.AddDays(d)) || IsOnLeave(_user.Id, d)) continue;
                int chance = 15 + (firstAge - age) * 50 / Math.Max(1, firstAge - lastAge);
                if (Random(d, 32) % 100 < chance)
                    Days[d].CopilotTurns = persona.ExpectedBand == AdoptionBand.Dormant ? 2 : Math.Max(1, perDay / 2);
            }
            int sequence = 0;
            foreach (var day in Days)
            {
                day.CopilotSequence = sequence;
                sequence += day.CopilotTurns;
            }
        }

        public int HostIndex(int day, int slot)
        {
            if (!_user.CopilotLicensed) return 0;
            if (_user.Persona.ExpectedBand == AdoptionBand.Champion && _user.Persona.InteractionsPerActiveDay >= 5
                && _user.Persona.DistinctApps >= 3 && _user.Id % 3 == 0 && day >= Days.Length - 28 && slot == 0) return 7;
            int breadth = Math.Max(1, _user.Persona.DistinctApps);
            return (_user.Department + (Days[day].CopilotSequence + slot) % breadth) % 7;
        }

        public int Agent(int day, int slot)
        {
            if (!_user.CopilotLicensed) return 0;
            if (HostIndex(day, slot) == 7) return 5;
            if (Random(day, 40 + slot) % 100 >= 35) return 0;
            int age = Days.Length - day;
            if (age <= 12 && _user.Id % 4 == 0) return 4;
            if (age >= 42 && age <= 80 && _user.Id % 4 == 1) return 2;
            if (age >= 96 && age <= 118 && _user.Id % 4 == 2) return 3;
            return 1;
        }
    }
}
