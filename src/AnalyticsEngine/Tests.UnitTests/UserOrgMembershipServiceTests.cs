extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserOrgs;
using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;

namespace Tests.UnitTests
{
    /// <summary>
    /// "Who is in each organisation?": the page rules and mapping in <see cref="UserOrgMembershipService"/>,
    /// and the search rules the SQL adapter applies. The SQL itself is covered by
    /// <see cref="UserOrgStoreSqlTests"/>.
    /// </summary>
    [TestClass]
    public class UserOrgMembershipServiceTests
    {
        private static UserOrgMembershipService Service(FakeReader reader, bool typeExists = true)
        {
            return new UserOrgMembershipService(new FakeTypeStore(typeExists), reader);
        }

        [TestMethod]
        public async Task Values_OfAnOrgTypeThatDoesNotExistAreNotFound()
        {
            var reader = new FakeReader();

            var result = await Service(reader, typeExists: false).ListValuesAsync(7, null, 1, 25, CancellationToken.None);

            Assert.IsNull(result, "The controller turns null into a 404.");
            Assert.AreEqual(0, reader.Calls.Count, "Nothing is queried for a type that is not there.");
        }

        [TestMethod]
        public async Task Values_AskForTheRequestedWindowAndMapEveryField()
        {
            var reader = new FakeReader
            {
                Values = new UserOrgValuePage
                {
                    TotalCount = 41,
                    Values = new[] { new UserOrgValueCount { Id = 9, Name = "Καλημέρα κόσμε", MemberCount = 1234 } },
                },
            };

            var result = await Service(reader).ListValuesAsync(7, "retail", 3, 10, CancellationToken.None);

            var call = reader.Calls.Single();
            Assert.AreEqual((7, (int?)null, "retail", 20, 10), call);
            Assert.AreEqual(3, result.Page);
            Assert.AreEqual(10, result.PageSize);
            Assert.AreEqual(41, result.Total);
            Assert.AreEqual(7, result.OrgTypeId);
            var row = result.Items.Single();
            Assert.AreEqual(9, row.Id);
            Assert.AreEqual("Καλημέρα κόσμε", row.Name, "Organisation names are tenant data and pass through untouched.");
            Assert.AreEqual(1234, row.MemberCount);
        }

        [TestMethod]
        public async Task Paging_ClampsWhateverArrivesOnTheQueryString()
        {
            var reader = new FakeReader();
            var service = Service(reader);

            var defaulted = await service.ListValuesAsync(7, null, 0, 0, CancellationToken.None);
            Assert.AreEqual(1, defaulted.Page, "Pages are 1-based; anything lower is the first page.");
            Assert.AreEqual(UserOrgMembershipService.DefaultValuesPageSize, defaulted.PageSize);
            Assert.AreEqual(0, reader.Calls.Last().Skip);

            var capped = await service.ListValuesAsync(7, null, -5, 5000, CancellationToken.None);
            Assert.AreEqual(UserOrgMembershipService.MaxPageSize, capped.PageSize, "A huge page size must not become a huge query.");

            var farAway = await service.ListValuesAsync(7, null, int.MaxValue, 200, CancellationToken.None);
            Assert.AreEqual(UserOrgMembershipService.MaxPage, farAway.Page);
            Assert.IsTrue(reader.Calls.Last().Skip > 0, "The rows to skip must not overflow into a negative number.");
        }

        [TestMethod]
        public async Task Members_UseTheirOwnDefaultPageSizeAndMapEveryField()
        {
            var reader = new FakeReader
            {
                Members = new UserOrgMemberPage
                {
                    OrgValueId = 9,
                    OrgValueName = "Retail",
                    TotalCount = 3,
                    Members = new[]
                    {
                        new UserOrgMember
                        {
                            UserId = 5,
                            UserPrincipalName = "amy@contoso.com",
                            Department = "Καλημέρα κόσμε",
                            JobTitle = "Account Manager",
                            AccountEnabled = false,
                        },
                    },
                },
            };

            var result = await Service(reader).ListMembersAsync(7, 9, null, 1, 0, CancellationToken.None);

            Assert.AreEqual((7, (int?)9, (string)null, 0, UserOrgMembershipService.DefaultMembersPageSize), reader.Calls.Single());
            Assert.AreEqual(9, result.ValueId);
            Assert.AreEqual("Retail", result.ValueName);
            Assert.AreEqual(3, result.Total);
            var member = result.Items.Single();
            Assert.AreEqual(5, member.UserId);
            Assert.AreEqual("amy@contoso.com", member.UserPrincipalName);
            Assert.AreEqual("Καλημέρα κόσμε", member.Department);
            Assert.AreEqual("Account Manager", member.JobTitle);
            Assert.AreEqual(false, member.AccountEnabled);
        }

        [TestMethod]
        public async Task Members_OfAnOrganisationThatDoesNotExistAreNotFound()
        {
            var result = await Service(new FakeReader()).ListMembersAsync(7, 9, null, 1, 50, CancellationToken.None);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void TheApiSpeaksCamelCase()
        {
            // The Web project has no camelCase resolver, so a property without [JsonProperty] reaches the
            // portal in PascalCase and reads as undefined.
            var values = JsonConvert.SerializeObject(new UserOrgValuePageModel
            {
                Items = new List<UserOrgValueRowModel> { new UserOrgValueRowModel() },
            });
            var members = JsonConvert.SerializeObject(new UserOrgMemberPageModel
            {
                Items = new List<UserOrgMemberModel> { new UserOrgMemberModel() },
            });

            foreach (var key in new[] { "orgTypeId", "page", "pageSize", "total", "items", "id", "name", "memberCount" })
            {
                StringAssert.Contains(values, $"\"{key}\":");
            }

            foreach (var key in new[] { "orgTypeId", "valueId", "valueName", "page", "pageSize", "total", "items", "userId", "userPrincipalName", "department", "jobTitle", "accountEnabled" })
            {
                StringAssert.Contains(members, $"\"{key}\":");
            }
        }

        [TestMethod]
        public void Search_IsTrimmedCappedAndBlankMeansEverything()
        {
            Assert.IsNull(UserOrgRules.NormaliseSearch(null));
            Assert.IsNull(UserOrgRules.NormaliseSearch("   "));
            Assert.AreEqual("retail", UserOrgRules.NormaliseSearch("  retail  "));

            var tooLong = new string('x', UserOrgRules.MaxSearchLength + 20);
            Assert.AreEqual(UserOrgRules.MaxSearchLength, UserOrgRules.NormaliseSearch(tooLong).Length);

            var emojiAtTheCut = new string('x', UserOrgRules.MaxSearchLength - 1) + "\U0001F600";
            var capped = UserOrgRules.NormaliseSearch(emojiAtTheCut);
            Assert.IsFalse(char.IsHighSurrogate(capped[capped.Length - 1]), "The cap must never leave half a character.");
        }

        [TestMethod]
        public void Search_EscapesEveryLikeWildcardSoItMatchesWhatWasTyped()
        {
            Assert.AreEqual(@"%50\%\_\[x]\\%", SqlUserOrgMembershipReader.ContainsPattern(@" 50%_[x]\ "));
            Assert.AreEqual("%Καλημέρα%", SqlUserOrgMembershipReader.ContainsPattern("Καλημέρα"));
            Assert.IsNull(SqlUserOrgMembershipReader.ContainsPattern(" "));
        }

        private static UserOrgAPIController Controller(IUserOrgMembershipReader reader, bool typeExists = true)
        {
            var service = new UserOrgMembershipService(new FakeTypeStore(typeExists), reader);
            return new UserOrgAPIController(() => throw new NotSupportedException(), () => service)
            {
                Request = new HttpRequestMessage(),
                Configuration = new HttpConfiguration(),
            };
        }

        [TestMethod]
        public async Task AnAbortedRequestIsNotReportedAsAFault()
        {
            // The browse panel aborts its request whenever the admin picks another organisation, page or
            // search. The cancellation can surface as any exception - SqlClient uses SqlException - so
            // the reader here throws something that is NOT an OperationCanceledException: it is the
            // request's token that must decide, or every click would reach Application Insights as an
            // "Unhandled web request error".
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var controller = Controller(new ThrowingReader());

                var result = await controller.GetValues(1, cts.Token);
                var response = await result.ExecuteAsync(CancellationToken.None);

                Assert.AreEqual(499, (int)response.StatusCode, "An abort gets no error body and no telemetry.");
            }
        }

        [TestMethod]
        public async Task AMissingTypeOrOrganisationIsA404()
        {
            var missingType = await (await Controller(new FakeReader(), typeExists: false)
                .GetValues(1, CancellationToken.None)).ExecuteAsync(CancellationToken.None);
            var missingValue = await (await Controller(new FakeReader())
                .GetMembers(1, 9, CancellationToken.None)).ExecuteAsync(CancellationToken.None);

            Assert.AreEqual(404, (int)missingType.StatusCode);
            Assert.AreEqual(404, (int)missingValue.StatusCode);
        }

        private sealed class ThrowingReader : IUserOrgMembershipReader
        {
            public Task<UserOrgValuePage> GetValuesAsync(int orgTypeId, string search, int skip, int take, CancellationToken cancellationToken = default(CancellationToken))
                => throw new InvalidOperationException("A severe error occurred on the current command.");

            public Task<UserOrgMemberPage> GetMembersAsync(int orgTypeId, int orgValueId, string search, int skip, int take, CancellationToken cancellationToken = default(CancellationToken))
                => throw new InvalidOperationException("A severe error occurred on the current command.");
        }

        private sealed class FakeReader : IUserOrgMembershipReader
        {
            public UserOrgValuePage Values { get; set; } = new UserOrgValuePage();

            public UserOrgMemberPage Members { get; set; }

            public List<(int OrgTypeId, int? OrgValueId, string Search, int Skip, int Take)> Calls { get; }
                = new List<(int OrgTypeId, int? OrgValueId, string Search, int Skip, int Take)>();

            public Task<UserOrgValuePage> GetValuesAsync(int orgTypeId, string search, int skip, int take, CancellationToken cancellationToken = default(CancellationToken))
            {
                Calls.Add((orgTypeId, null, search, skip, take));
                return Task.FromResult(Values);
            }

            public Task<UserOrgMemberPage> GetMembersAsync(int orgTypeId, int orgValueId, string search, int skip, int take, CancellationToken cancellationToken = default(CancellationToken))
            {
                Calls.Add((orgTypeId, orgValueId, search, skip, take));
                return Task.FromResult(Members);
            }
        }

        private sealed class FakeTypeStore : IUserOrgTypeStore
        {
            private readonly bool _exists;

            public FakeTypeStore(bool exists)
            {
                _exists = exists;
            }

            public Task<UserOrgType> GetAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
                => Task.FromResult(_exists ? new UserOrgType { Id = id, Name = "Team", SourceKind = UserOrgSourceKind.CsvUpload } : null);

            public Task<IReadOnlyList<UserOrgType>> GetAllAsync(CancellationToken cancellationToken = default(CancellationToken))
                => throw new NotSupportedException();

            public Task<IReadOnlyList<UserOrgTypeSummary>> GetSummariesAsync(CancellationToken cancellationToken = default(CancellationToken))
                => throw new NotSupportedException();

            public Task<IReadOnlyList<UserOrgType>> GetEnabledEntraTypesAsync(CancellationToken cancellationToken = default(CancellationToken))
                => throw new NotSupportedException();

            public Task<int> CreateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken))
                => throw new NotSupportedException();

            public Task UpdateAsync(UserOrgType type, bool clearAssignments, bool bumpGeneration, CancellationToken cancellationToken = default(CancellationToken))
                => throw new NotSupportedException();

            public Task DeleteAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
                => throw new NotSupportedException();

            public Task<int> RecordEntraRefreshAsync(IReadOnlyDictionary<int, int> expectedGenerations, DateTime refreshedUtc, CancellationToken cancellationToken = default(CancellationToken))
                => throw new NotSupportedException();
        }
    }
}
