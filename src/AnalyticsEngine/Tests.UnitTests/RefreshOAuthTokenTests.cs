using Common.Entities.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace Tests.UnitTests
{
    [TestClass]
    public class RefreshOAuthTokenTests
    {
        [TestMethod]
        public void AuthorizationCodeResponse_Preserves_IdToken_For_Oidc_Validation()
        {
            var token = JsonConvert.DeserializeObject<RefreshOAuthToken>(
                "{\"access_token\":\"synthetic-access-token\",\"refresh_token\":\"synthetic-refresh-token\",\"id_token\":\"synthetic-id-token\"}");

            Assert.IsNotNull(token);
            Assert.AreEqual("synthetic-access-token", token.AccessToken);
            Assert.AreEqual("synthetic-refresh-token", token.RefreshToken);
            Assert.AreEqual("synthetic-id-token", token.IdToken);
        }

        [TestMethod]
        public void RefreshTokenResponse_Without_IdToken_Remains_Readable()
        {
            var token = JsonConvert.DeserializeObject<RefreshOAuthToken>(
                "{\"access_token\":\"synthetic-access-token\",\"refresh_token\":\"synthetic-refresh-token\"}");

            Assert.IsNotNull(token);
            Assert.AreEqual("synthetic-access-token", token.AccessToken);
            Assert.AreEqual("synthetic-refresh-token", token.RefreshToken);
            Assert.IsNull(token.IdToken);
        }
    }
}
