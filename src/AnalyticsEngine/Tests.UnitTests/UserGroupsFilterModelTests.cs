using Common.Entities.Config;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    [TestClass]
    public class UserGroupsFilterModelTests
    {
        [TestMethod]
        public void Patterns_AreSplitCorrectly()
        {
            var filter = new UserGroupsFilterModel("GroupA;GroupB;GroupC*");
            Assert.AreEqual(3, filter.Patterns.Count);
            Assert.AreEqual("GroupA", filter.Patterns[0]);
            Assert.AreEqual("GroupB", filter.Patterns[1]);
            Assert.AreEqual("GroupC*", filter.Patterns[2]);
        }

        [TestMethod]
        public void Matches_ExactMatch_ReturnsTrue()
        {
            var filter = new UserGroupsFilterModel("GroupA;GroupB");
            Assert.IsTrue(filter.Matches("GroupA"));
            Assert.IsTrue(filter.Matches("GroupB"));
            Assert.IsFalse(filter.Matches("GroupC"));
        }

        [TestMethod]
        public void Matches_WildcardMatch_ReturnsTrue()
        {
            var filter = new UserGroupsFilterModel("Group*");
            Assert.IsTrue(filter.Matches("GroupA"));
            Assert.IsTrue(filter.Matches("Group123"));
            Assert.IsFalse(filter.Matches("OtherGroup"));
        }

        [TestMethod]
        public void Matches_EmptyOrNull_ReturnsFalse()
        {
            var filter = new UserGroupsFilterModel("");
            Assert.IsFalse(filter.Matches("GroupA"));
            Assert.IsFalse(filter.Matches(""));
            Assert.IsFalse(filter.Matches(null));
        }

        [TestMethod]
        public void Matches_MixedPatterns()
        {
            var filter = new UserGroupsFilterModel("Admin*;*Users;*Test*");
            Assert.IsTrue(filter.Matches("AdminGroup"));
            Assert.IsTrue(filter.Matches("PowerUsers"));
            Assert.IsTrue(filter.Matches("MyTestGroup"));
            Assert.IsFalse(filter.Matches("RandomGroup"));
        }
    }
}
