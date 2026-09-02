using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the Graph user -> analytics user mapping rules. These were previously interleaved with
    /// EF change tracking and the lookup caches inside <c>UserDataMapper.UpdateUserMetadata</c> and had
    /// no test of their own, despite being the mapping the whole user pipeline depends on. They now run
    /// with zero SQL Server and zero Graph dependency. See issues #371 / #381.
    /// </summary>
    [TestClass]
    public class UserMetadataMappingRulesTests
    {
        private static GraphUser FullyPopulatedGraphUser()
        {
            return new GraphUser
            {
                Id = "00000000-0000-0000-0000-000000000001",
                UserPrincipalName = "jane.doe@contoso.com",
                Mail = "jane.doe@contoso.com",
                AccountEnabled = true,
                PostalCode = "SW1A 1AA",
                Department = "Engineering",
                JobTitle = "Principal Engineer",
                OfficeLocation = "London",
                UsageLocation = "GB",
                Country = "United Kingdom",
                State = "Greater London",
                CompanyName = "Contoso",
            };
        }

        [TestMethod]
        public void UserMapping_DepartmentJobTitleOfficeCountryState_AreMappedFromGraphUser()
        {
            var plan = UserMetadataMappingRules.BuildPlan(FullyPopulatedGraphUser());

            Assert.AreEqual("Engineering", plan.DepartmentName);
            Assert.AreEqual("Principal Engineer", plan.JobTitleName);
            Assert.AreEqual("London", plan.OfficeLocationName);
            Assert.AreEqual("GB", plan.UsageLocationName);
            Assert.AreEqual("United Kingdom", plan.CountryName);
            Assert.AreEqual("Greater London", plan.StateOrProvinceName);
            Assert.AreEqual("Contoso", plan.CompanyName);
        }

        [TestMethod]
        public void UserMapping_DirectFields_AreTakenFromTheGraphUser()
        {
            var plan = UserMetadataMappingRules.BuildPlan(FullyPopulatedGraphUser());

            Assert.AreEqual(true, plan.AccountEnabled);
            Assert.AreEqual("SW1A 1AA", plan.PostalCode);
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", plan.AzureAdId);
            Assert.AreEqual("jane.doe@contoso.com", plan.Mail);
        }

        /// <summary>
        /// The pipeline deliberately CLEARS a lookup when Graph reports nothing for it - the comment in
        /// <c>UserDataMapper</c> explains why both the navigation property and the foreign key have to be
        /// nulled. A null plan value is that instruction.
        /// </summary>
        /// <remarks>
        /// Issue #371 named this test "NullGraphFields_DoNotOverwriteExistingDbValues", which is the
        /// opposite of what the code does. The behaviour here is the real one; changing it would be a
        /// behavioural change, which #381 forbids.
        /// </remarks>
        [TestMethod]
        public void UserMapping_MissingGraphFields_AskForTheLookupToBeCleared()
        {
            foreach (var missing in new[] { null, "", "   ", "\t\r\n" })
            {
                var plan = UserMetadataMappingRules.BuildPlan(new GraphUser
                {
                    Department = missing,
                    JobTitle = missing,
                    OfficeLocation = missing,
                    UsageLocation = missing,
                    Country = missing,
                    State = missing,
                    CompanyName = missing,
                });

                var label = missing == null ? "(null)" : $"'{missing}'";
                Assert.IsNull(plan.DepartmentName, $"department should clear for {label}");
                Assert.IsNull(plan.JobTitleName, $"job title should clear for {label}");
                Assert.IsNull(plan.OfficeLocationName, $"office location should clear for {label}");
                Assert.IsNull(plan.UsageLocationName, $"usage location should clear for {label}");
                Assert.IsNull(plan.CountryName, $"country should clear for {label}");
                Assert.IsNull(plan.StateOrProvinceName, $"state should clear for {label}");
                Assert.IsNull(plan.CompanyName, $"company should clear for {label}");
            }
        }

        [TestMethod]
        public void UserMapping_LookupValues_AreTrimmed()
        {
            var plan = UserMetadataMappingRules.BuildPlan(new GraphUser { Department = "  Engineering \t" });

            Assert.AreEqual("Engineering", plan.DepartmentName,
                "untrimmed values would create a second lookup row for the same department");
        }

        /// <summary>
        /// The lookup name columns are 100 characters wide, so an over-long value has to be cut before it
        /// reaches SQL or the insert fails.
        /// </summary>
        [TestMethod]
        public void UserMapping_OverlongLookupValues_AreCappedAtTheColumnWidth()
        {
            var tooLong = new string('x', UserMetadataMappingRules.LookupNameMaxLength + 50);

            var plan = UserMetadataMappingRules.BuildPlan(new GraphUser { Department = tooLong });

            Assert.AreEqual(UserMetadataMappingRules.LookupNameMaxLength, plan.DepartmentName.Length);
            StringAssert.EndsWith(plan.DepartmentName, "...", "the pipeline marks a truncated value with an ellipsis");
            Assert.AreEqual(StringUtils.EnsureMaxLength(tooLong, UserMetadataMappingRules.LookupNameMaxLength), plan.DepartmentName,
                "capping must stay identical to the shared StringUtils rule the pipeline has always used");
        }

        [TestMethod]
        public void UserMapping_ValueExactlyAtTheLimit_IsNotTruncated()
        {
            var exact = new string('x', UserMetadataMappingRules.LookupNameMaxLength);

            var plan = UserMetadataMappingRules.BuildPlan(new GraphUser { Department = exact });

            Assert.AreEqual(exact, plan.DepartmentName);
        }

        /// <summary>
        /// Department names, job titles, office locations, countries and company names come from Entra
        /// as free text with no character restriction, so they are routinely non-Latin. Nothing in the
        /// mapping may fold or mangle them - see the character-set rule in the repo's C# instructions.
        /// </summary>
        /// <remarks>
        /// The UPN is deliberately ASCII here. Entra restricts <c>userPrincipalName</c> to
        /// <c>A-Z a-z 0-9 ' . - _ ! # ^ ~</c> and explicitly disallows accented characters, so a Greek
        /// UPN is not a real tenant case and asserting one would be testing data the pipeline does not
        /// receive. The unrestricted fields above are where the real risk is.
        /// </remarks>
        [TestMethod]
        public void UserMapping_ProfileLookupValues_RoundTripNonAsciiValues()
        {
            var plan = UserMetadataMappingRules.BuildPlan(new GraphUser
            {
                UserPrincipalName = "kalimera@contoso.onmicrosoft.com",
                Mail = "kalimera@contoso.onmicrosoft.com",
                CompanyName = "Καλημέρα κόσμε",
                UsageLocation = "GR",
                Department = "Μηχανική",
                OfficeLocation = "Αθήνα",
                Country = "Ελλάδα",
                State = "Αττική",
                JobTitle = "Μηχανικός",
            });

            Assert.AreEqual("Καλημέρα κόσμε", plan.CompanyName);
            Assert.AreEqual("GR", plan.UsageLocationName);
            Assert.AreEqual("Μηχανική", plan.DepartmentName);
            Assert.AreEqual("Αθήνα", plan.OfficeLocationName);
            Assert.AreEqual("Ελλάδα", plan.CountryName);
            Assert.AreEqual("Αττική", plan.StateOrProvinceName);
            Assert.AreEqual("Μηχανικός", plan.JobTitleName);
        }

        [TestMethod]
        public void UserMapping_ManagerAadId_IsTakenFromGraphAndIsNullWhenThereIsNoManager()
        {
            var withManager = FullyPopulatedGraphUser();
            withManager.ManagerInfo.Add(new ManagerInfo { Id = "00000000-0000-0000-0000-000000000002" });

            Assert.AreEqual("00000000-0000-0000-0000-000000000002",
                UserMetadataMappingRules.BuildPlan(withManager).ManagerAadId);

            Assert.IsNull(UserMetadataMappingRules.BuildPlan(FullyPopulatedGraphUser()).ManagerAadId,
                "no manager from Graph must read as null - that is what clears the relationship");
        }

        /// <summary>
        /// The mapping is a pure function of the Graph user: the same input must always give the same
        /// plan. That is what lets the pipeline be re-run over the same delta window safely.
        /// </summary>
        [TestMethod]
        public void UserMapping_SameGraphUser_ProducesAnIdenticalPlan()
        {
            var graphUser = FullyPopulatedGraphUser();

            var first = UserMetadataMappingRules.BuildPlan(graphUser);
            var second = UserMetadataMappingRules.BuildPlan(graphUser);

            Assert.AreEqual(first.DepartmentName, second.DepartmentName);
            Assert.AreEqual(first.JobTitleName, second.JobTitleName);
            Assert.AreEqual(first.OfficeLocationName, second.OfficeLocationName);
            Assert.AreEqual(first.UsageLocationName, second.UsageLocationName);
            Assert.AreEqual(first.CountryName, second.CountryName);
            Assert.AreEqual(first.StateOrProvinceName, second.StateOrProvinceName);
            Assert.AreEqual(first.CompanyName, second.CompanyName);
            Assert.AreEqual(first.ManagerAadId, second.ManagerAadId);
            Assert.AreEqual(first.AzureAdId, second.AzureAdId);
            Assert.AreNotSame(first, second, "each call must build its own plan, not hand back a shared one");
        }

        [TestMethod]
        public void UserMapping_AccountEnabledUnknown_StaysNullRatherThanBecomingFalse()
        {
            // Graph omits accountEnabled for some directory objects. Defaulting it to false would mark
            // live users as disabled and drop them out of adoption reporting.
            var plan = UserMetadataMappingRules.BuildPlan(new GraphUser { AccountEnabled = null });

            Assert.IsNull(plan.AccountEnabled);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void UserMapping_NullGraphUser_IsRejected()
        {
            UserMetadataMappingRules.BuildPlan(null);
        }

        [TestMethod]
        public void NormaliseLookupName_TrimsCapsAndTreatsEmptyAsClear()
        {
            Assert.IsNull(UserMetadataMappingRules.NormaliseLookupName(null));
            Assert.IsNull(UserMetadataMappingRules.NormaliseLookupName("   "));
            Assert.AreEqual("Sales", UserMetadataMappingRules.NormaliseLookupName(" Sales "));
            Assert.AreEqual(UserMetadataMappingRules.LookupNameMaxLength,
                UserMetadataMappingRules.NormaliseLookupName(new string('y', 500)).Length);
        }
    }
}
