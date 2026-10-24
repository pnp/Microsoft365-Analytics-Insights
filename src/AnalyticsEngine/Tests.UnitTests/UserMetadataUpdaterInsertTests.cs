using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for InsertMissingUsers and bulk insert logic
    /// </summary>
    [TestClass]
    public class UserMetadataUpdaterInsertTests
    {
        [TestMethod]
        public async Task UserMetadataUpdater_RenamedUser_KeepsTheSameRowAndAuditHistory()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var objectId = Guid.NewGuid().ToString();
            var oldUpn = "old-" + suffix + "@contoso.com";
            var newUpn = "new-" + suffix + "@contoso.com";
            var eventId = Guid.NewGuid();
            int userId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User { UserPrincipalName = oldUpn, AzureAdId = objectId, AccountEnabled = true };
                db.users.Add(user);
                await db.SaveChangesAsync();
                userId = user.ID;
                db.AuditEventsCommon.Add(new CommonAuditEvent
                {
                    Id = eventId, UserId = userId, OperationId = null, TimeStamp = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { Id = objectId, UserPrincipalName = newUpn, AccountEnabled = true, Mail = newUpn }
            });

            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .InsertAndUpdateDatabaseFromExternalUsers());

            using (var db = new AnalyticsEntitiesContext())
            {
                var user = await db.users.SingleAsync(u => u.ID == userId);
                Assert.AreEqual(newUpn, user.UserPrincipalName);
                Assert.AreEqual(newUpn, user.Mail);
                Assert.AreEqual(1, await db.users.CountAsync(u => u.AzureAdId == objectId));
                Assert.AreEqual(userId, (await db.AuditEventsCommon.SingleAsync(e => e.Id == eventId)).UserId);
            }
        }

        /// <summary>
        /// C-1: the true conflict - the user's new sign-in name is on a row carrying another object id. Only that user is
        /// quarantined: both rows are left as they are, everyone else in the read is imported, and the checkpoint moves.
        /// It used to stop the whole user import, every cycle.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_RenameConflict_QuarantinesOnlyThatUser_AndTheImportCompletes()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var objectId = Guid.NewGuid().ToString();
            var otherObjectId = Guid.NewGuid().ToString();
            var oldUpn = "old-conflict-" + suffix + "@contoso.com";
            var newUpn = "new-conflict-" + suffix + "@contoso.com";
            var newcomerUpn = "newcomer-conflict-" + suffix + "@contoso.com";
            var colleagueUpn = "colleague-conflict-" + suffix + "@contoso.com";
            var colleagueId = Guid.NewGuid().ToString();
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new User { UserPrincipalName = oldUpn, AzureAdId = objectId, AccountEnabled = true });
                db.users.Add(new User { UserPrincipalName = newUpn, AzureAdId = otherObjectId, AccountEnabled = true });
                db.users.Add(new User { UserPrincipalName = colleagueUpn, AzureAdId = colleagueId, AccountEnabled = true, Mail = "before@contoso.com" });
                await db.SaveChangesAsync();
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { Id = objectId, UserPrincipalName = newUpn, AccountEnabled = true, Mail = newUpn },
                new GraphUser { Id = Guid.NewGuid().ToString(), UserPrincipalName = newcomerUpn, AccountEnabled = true },
                new GraphUser { Id = colleagueId, UserPrincipalName = colleagueUpn, AccountEnabled = true, Mail = colleagueUpn },
            });

            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .InsertAndUpdateDatabaseFromExternalUsers(), "One user's identity conflict must not stop the user import.");
            Assert.AreEqual("fake-new-delta", await loader.DeltaValueProvider.GetDeltaToken(), "The checkpoint moves; the quarantined user is kept aside.");
            using (var db = new AnalyticsEntitiesContext())
            {
                var oldRow = await db.users.SingleAsync(u => u.UserPrincipalName == oldUpn);
                var heldRow = await db.users.SingleAsync(u => u.UserPrincipalName == newUpn);
                Assert.AreEqual(objectId, oldRow.AzureAdId);
                Assert.AreEqual(otherObjectId, heldRow.AzureAdId, "The other account's row is not taken over.");
                Assert.AreNotEqual(newUpn, heldRow.Mail, "Nothing from the quarantined user is written to it.");
                Assert.IsTrue(await db.users.AnyAsync(u => u.UserPrincipalName == newcomerUpn), "Everyone else is still imported.");
                Assert.AreEqual(colleagueUpn, (await db.users.SingleAsync(u => u.UserPrincipalName == colleagueUpn)).Mail);
            }
        }

        /// <summary>
        /// C-1: other imports add a row by sign-in name alone (no object id, which the entity defaults to "") the first time
        /// they see someone. A renamed user who was active under the new name before the next user import used to stop the
        /// whole import, every cycle. That row is adopted - given the object id, as before in-place renames existed - and
        /// the user's original row keeps its history.
        /// </summary>
        [DataTestMethod]
        [DataRow(null, DisplayName = "Placeholder row with a NULL object id")]
        [DataRow("", DisplayName = "Placeholder row with an empty object id")]
        public async Task UserMetadataUpdater_RenamedUsersNewNameHasAPlaceholderRow_AdoptsIt_AndTheImportCompletes(string placeholderObjectId)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var objectId = Guid.NewGuid().ToString();
            var oldUpn = "old-placeholder-" + suffix + "@contoso.com";
            var newUpn = "new-placeholder-" + suffix + "@contoso.com";
            var eventId = Guid.NewGuid();
            int originalId, placeholderId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var original = new User { UserPrincipalName = oldUpn, AzureAdId = objectId, AccountEnabled = true };
                var placeholder = new User { UserPrincipalName = newUpn, AzureAdId = placeholderObjectId };
                db.users.Add(original);
                db.users.Add(placeholder);
                await db.SaveChangesAsync();
                originalId = original.ID;
                placeholderId = placeholder.ID;
                db.AuditEventsCommon.Add(new CommonAuditEvent { Id = eventId, UserId = originalId, OperationId = null, TimeStamp = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { Id = objectId, UserPrincipalName = newUpn, AccountEnabled = true, Mail = newUpn }
            });

            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .InsertAndUpdateDatabaseFromExternalUsers());
            Assert.AreEqual("fake-new-delta", await loader.DeltaValueProvider.GetDeltaToken());
            using (var db = new AnalyticsEntitiesContext())
            {
                var placeholder = await db.users.SingleAsync(u => u.ID == placeholderId);
                Assert.AreEqual(objectId, placeholder.AzureAdId, "The row under the new name is the user's now.");
                Assert.AreEqual(newUpn, placeholder.Mail);
                var original = await db.users.SingleAsync(u => u.ID == originalId);
                Assert.AreEqual(oldUpn, original.UserPrincipalName, "The original row stays as history.");
                Assert.AreEqual(originalId, (await db.AuditEventsCommon.SingleAsync(e => e.Id == eventId)).UserId);
                Assert.AreEqual(2, await db.users.CountAsync(u => u.AzureAdId == objectId), "No third row.");
            }
        }

        /// <summary>
        /// Several rows already carry the object id (renames by earlier versions) and nobody has the new name: the newest
        /// row - the one manager links and the Teams member map already treat as the user's - is renamed in place.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_SeveralRowsCarryTheObjectId_TheNewestIsRenamedInPlace()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var objectId = Guid.NewGuid().ToString();
            var firstUpn = "first-" + suffix + "@contoso.com";
            var secondUpn = "second-" + suffix + "@contoso.com";
            var currentUpn = "current-" + suffix + "@contoso.com";
            int firstId, secondId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var first = new User { UserPrincipalName = firstUpn, AzureAdId = objectId, AccountEnabled = true };
                db.users.Add(first);
                await db.SaveChangesAsync();
                var second = new User { UserPrincipalName = secondUpn, AzureAdId = objectId, AccountEnabled = true };
                db.users.Add(second);
                await db.SaveChangesAsync();
                firstId = first.ID;
                secondId = second.ID;
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { Id = objectId, UserPrincipalName = currentUpn, AccountEnabled = true }
            });
            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .InsertAndUpdateDatabaseFromExternalUsers());
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(2, await db.users.CountAsync(u => u.AzureAdId == objectId), "No third row.");
                Assert.AreEqual(currentUpn, (await db.users.SingleAsync(u => u.ID == secondId)).UserPrincipalName);
                Assert.AreEqual(firstUpn, (await db.users.SingleAsync(u => u.ID == firstId)).UserPrincipalName);
            }
        }

        /// <summary>
        /// A rename chain in one read: A takes B's old name while B moves on to a new one. B is renamed first, then A, and
        /// both keep their rows.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_RenameChainInOneRead_RenamesBothInPlace()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var aId = Guid.NewGuid().ToString();
            var bId = Guid.NewGuid().ToString();
            var name1 = "chain1-" + suffix + "@contoso.com";
            var name2 = "chain2-" + suffix + "@contoso.com";
            var name3 = "chain3-" + suffix + "@contoso.com";
            int aRow, bRow;
            using (var db = new AnalyticsEntitiesContext())
            {
                var a = new User { UserPrincipalName = name1, AzureAdId = aId, AccountEnabled = true };
                var b = new User { UserPrincipalName = name2, AzureAdId = bId, AccountEnabled = true };
                db.users.Add(a);
                db.users.Add(b);
                await db.SaveChangesAsync();
                aRow = a.ID;
                bRow = b.ID;
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { Id = aId, UserPrincipalName = name2, AccountEnabled = true },
                new GraphUser { Id = bId, UserPrincipalName = name3, AccountEnabled = true },
            });

            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .InsertAndUpdateDatabaseFromExternalUsers());
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(name2, (await db.users.SingleAsync(u => u.ID == aRow)).UserPrincipalName);
                Assert.AreEqual(name3, (await db.users.SingleAsync(u => u.ID == bRow)).UserPrincipalName);
                Assert.AreEqual(1, await db.users.CountAsync(u => u.AzureAdId == aId));
                Assert.AreEqual(1, await db.users.CountAsync(u => u.AzureAdId == bId));
            }
        }

        /// <summary>
        /// C-3: after an in-place rename, a late audit or usage row under the old name creates a row with no object id.
        /// Renaming the user back to the old name then adopts that row, and the import completes.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_RenamedBackAfterLateActivityUnderTheOldName_AdoptsThatRow_AndTheImportCompletes()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var objectId = Guid.NewGuid().ToString();
            var firstUpn = "back-first-" + suffix + "@contoso.com";
            var secondUpn = "back-second-" + suffix + "@contoso.com";
            int rowId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User { UserPrincipalName = firstUpn, AzureAdId = objectId, AccountEnabled = true };
                db.users.Add(user);
                await db.SaveChangesAsync();
                rowId = user.ID;
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser> { new GraphUser { Id = objectId, UserPrincipalName = firstUpn, AccountEnabled = true } });
            await loader.DeltaValueProvider.SetDeltaToken("an-earlier-checkpoint");
            loader.DeltaUsersOverride = new List<GraphUser> { new GraphUser { Id = objectId, UserPrincipalName = secondUpn, AccountEnabled = true } };
            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(), "Renamed");

            // A late audit row under the old name: the activity import adds a row by name alone.
            int lateRowId;
            using (var db = new AnalyticsEntitiesContext())
            {
                var late = new User { UserPrincipalName = firstUpn, AzureAdId = null };
                db.users.Add(late);
                await db.SaveChangesAsync();
                lateRowId = late.ID;
            }

            loader.DeltaUsersOverride = new List<GraphUser> { new GraphUser { Id = objectId, UserPrincipalName = firstUpn, AccountEnabled = true, Mail = firstUpn } };
            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers(),
                "Renaming back must not stop the user import.");
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(secondUpn, (await db.users.SingleAsync(u => u.ID == rowId)).UserPrincipalName, "The renamed row keeps its history.");
                var late = await db.users.SingleAsync(u => u.ID == lateRowId);
                Assert.AreEqual(objectId, late.AzureAdId);
                Assert.AreEqual(firstUpn, late.Mail);
            }
        }

        /// <summary>
        /// Two users swapping sign-in names in one read can't be renamed one after the other (the names are unique), so
        /// both are quarantined - and nobody else waits for them.
        /// </summary>
        [TestMethod]
        public async Task UserMetadataUpdater_TwoUsersSwapNames_BothQuarantined_AndTheImportCompletes()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var aId = Guid.NewGuid().ToString();
            var bId = Guid.NewGuid().ToString();
            var name1 = "swap1-" + suffix + "@contoso.com";
            var name2 = "swap2-" + suffix + "@contoso.com";
            var newcomerUpn = "swap-newcomer-" + suffix + "@contoso.com";
            using (var db = new AnalyticsEntitiesContext())
            {
                db.users.Add(new User { UserPrincipalName = name1, AzureAdId = aId, AccountEnabled = true });
                db.users.Add(new User { UserPrincipalName = name2, AzureAdId = bId, AccountEnabled = true });
                await db.SaveChangesAsync();
            }
            var loader = new FakeUserMetadataLoader(new List<GraphUser>
            {
                new GraphUser { Id = aId, UserPrincipalName = name2, AccountEnabled = true },
                new GraphUser { Id = bId, UserPrincipalName = name1, AccountEnabled = true },
                new GraphUser { Id = Guid.NewGuid().ToString(), UserPrincipalName = newcomerUpn, AccountEnabled = true },
            });

            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader)
                .InsertAndUpdateDatabaseFromExternalUsers());
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(aId, (await db.users.SingleAsync(u => u.UserPrincipalName == name1)).AzureAdId);
                Assert.AreEqual(bId, (await db.users.SingleAsync(u => u.UserPrincipalName == name2)).AzureAdId);
                Assert.IsTrue(await db.users.AnyAsync(u => u.UserPrincipalName == newcomerUpn));
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_InsertsNewUsersOnly()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "newuser1@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = "newuser2@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = "existinguser@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                var existingTestUsers = await db.users
                    .Where(u => u.UserPrincipalName == "newuser1@test.com"
                             || u.UserPrincipalName == "newuser2@test.com"
                             || u.UserPrincipalName == "existinguser@test.com")
                    .ToListAsync();

                if (existingTestUsers.Any())
                {
                    db.users.RemoveRange(existingTestUsers);
                    await db.SaveChangesAsync();
                }

                var existingUser = new Common.Entities.User
                {
                    UserPrincipalName = "existinguser@test.com",
                    AzureAdId = graphUsers[2].Id
                };

                var existingDbUsers = new List<Common.Entities.User> { existingUser };

                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, existingDbUsers, false);

                Assert.AreEqual(2, insertedUsers.Count);
                Assert.IsTrue(insertedUsers.Any(u => u.UserPrincipalName == "newuser1@test.com"));
                Assert.IsTrue(insertedUsers.Any(u => u.UserPrincipalName == "newuser2@test.com"));
                Assert.IsFalse(insertedUsers.Any(u => u.UserPrincipalName == "existinguser@test.com"));
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_IgnoresUsersWithoutUPN()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "validuser@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = null, Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = "", Id = Guid.NewGuid().ToString(), AccountEnabled = true }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await db.users
                    .Where(u => u.UserPrincipalName == "validuser@test.com")
                    .FirstOrDefaultAsync();

                if (existingTestUser != null)
                {
                    db.users.Remove(existingTestUser);
                    await db.SaveChangesAsync();
                }

                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, new List<Common.Entities.User>(), false);

                Assert.AreEqual(1, insertedUsers.Count);
                Assert.AreEqual("validuser@test.com", insertedUsers[0].UserPrincipalName);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_CaseInsensitiveUPNComparison()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "User@Test.COM", Id = Guid.NewGuid().ToString(), AccountEnabled = true }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                var existingUser = new Common.Entities.User
                {
                    UserPrincipalName = "user@test.com",
                    AzureAdId = graphUsers[0].Id
                };

                var existingDbUsers = new List<Common.Entities.User> { existingUser };

                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, existingDbUsers, false);

                Assert.AreEqual(0, insertedUsers.Count);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_UpdatesUserMetadata()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var graphUsers = new List<GraphUser>
            {
                new GraphUser
                {
                    UserPrincipalName = "newuser@test.com",
                    Id = userId,
                    AccountEnabled = true,
                    Department = "IT",
                    JobTitle = "Developer",
                    OfficeLocation = "Building 1",
                    Mail = "newuser@test.com",
                    PostalCode = "12345"
                }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await db.users
                    .Where(u => u.UserPrincipalName == "newuser@test.com")
                    .FirstOrDefaultAsync();

                if (existingTestUser != null)
                {
                    db.users.Remove(existingTestUser);
                    await db.SaveChangesAsync();
                }

                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, new List<Common.Entities.User>(), false);

                Assert.AreEqual(1, insertedUsers.Count);
                var insertedUser = insertedUsers[0];

                Assert.AreEqual("newuser@test.com", insertedUser.UserPrincipalName);
                Assert.AreEqual(userId, insertedUser.AzureAdId);
                Assert.AreEqual(true, insertedUser.AccountEnabled);
                Assert.AreEqual("newuser@test.com", insertedUser.Mail);
                Assert.AreEqual("12345", insertedUser.PostalCode);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_HandlesLargeNumberOfUsers()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var graphUsers = new List<GraphUser>();
            for (int i = 0; i < 100; i++)
            {
                graphUsers.Add(new GraphUser
                {
                    UserPrincipalName = $"user{i}@test.com",
                    Id = Guid.NewGuid().ToString(),
                    AccountEnabled = true
                });
            }

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                var existingTestUsers = await db.users
                    .Where(u => u.UserPrincipalName.StartsWith("user") && u.UserPrincipalName.EndsWith("@test.com"))
                    .ToListAsync();

                if (existingTestUsers.Any())
                {
                    db.users.RemoveRange(existingTestUsers);
                    await db.SaveChangesAsync();
                }

                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, new List<Common.Entities.User>(), false);

                Assert.AreEqual(100, insertedUsers.Count);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_EmptyGraphUsersList_ReturnsEmpty()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();
            var fakeLoader = new FakeUserMetadataLoader();
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                var insertedUsers = await updater.InsertMissingUsers(db, new List<GraphUser>(), new List<Common.Entities.User>(), false);

                Assert.AreEqual(0, insertedUsers.Count);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_InsertMissingUsers_AllUsersAlreadyExist_ReturnsEmpty()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = "existing1@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true },
                new GraphUser { UserPrincipalName = "existing2@test.com", Id = Guid.NewGuid().ToString(), AccountEnabled = true }
            };

            var existingDbUsers = new List<Common.Entities.User>
            {
                new Common.Entities.User { UserPrincipalName = "existing1@test.com", AzureAdId = graphUsers[0].Id },
                new Common.Entities.User { UserPrincipalName = "existing2@test.com", AzureAdId = graphUsers[1].Id }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            using (var db = new AnalyticsEntitiesContext())
            {
                var insertedUsers = await updater.InsertMissingUsers(db, graphUsers, existingDbUsers, false);

                Assert.AreEqual(0, insertedUsers.Count);
            }
        }

        [TestMethod]
        public async Task UserMetadataUpdater_DuplicateUPNInGraphUsers_HandledGracefully()
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var config = new AppConfig();

            var userId = Guid.NewGuid().ToString();
            var userUpn = $"dupeupn{DateTime.Now.Ticks}@test.com";

            using (var cleanupDb = new AnalyticsEntitiesContext())
            {
                var existingTestUser = await cleanupDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .FirstOrDefaultAsync();

                if (existingTestUser != null)
                {
                    cleanupDb.users.Remove(existingTestUser);
                    await cleanupDb.SaveChangesAsync();
                }
            }

            var graphUsers = new List<GraphUser>
            {
                new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn },
                new GraphUser { UserPrincipalName = userUpn, Id = userId, AccountEnabled = true, Mail = userUpn }
            };

            var fakeLoader = new FakeUserMetadataLoader(graphUsers);
            var updater = new UserMetadataUpdater(logger, config, fakeLoader);

            await updater.InsertAndUpdateDatabaseFromExternalUsers();

            using (var verifyDb = new AnalyticsEntitiesContext())
            {
                var allUsers = await verifyDb.users
                    .Where(u => u.UserPrincipalName == userUpn)
                    .ToListAsync();

                Assert.IsTrue(allUsers.Count <= 1, "Should not create duplicate users from duplicate Graph entries");
            }
        }
    }
}
