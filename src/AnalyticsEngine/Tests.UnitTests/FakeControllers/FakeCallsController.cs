using Common.DataUtils;
using Common.Entities.Config;
using Microsoft.Graph;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests.FakeControllers
{
    public class FakeCallsController : ApiController
    {
        [HttpGet]
        [Route("v1.0/communications/callRecords/{id}")]
        public async Task<HttpResponseMessage> Call(string id)
        {
            var authConfig = new AppConfig();
            var _auth = new GraphAppIndentityOAuthContext(AnalyticsLogger.ConsoleOnlyTracer(), authConfig.ClientID, authConfig.TenantGUID.ToString(), authConfig.ClientSecret, authConfig.KeyVaultUrl, authConfig.UseClientCertificate);
            await _auth.InitClientCredential();

            var graphClient = new GraphServiceClient(_auth.Creds);

            // Get Adele user from Graph & insert blanks into DB 
            var graphUsersFirstPage = await graphClient.Users.Request().GetAsync();
            var user1 = graphUsersFirstPage[0];
            var user2 = graphUsersFirstPage[1];

            var call = new CallRecordDTO()
            {
                GraphCallID = id,
                Organizer = new IdentitySetDTO { User = new UserDTO { Id = user1.Id } },
                Sessions = new List<CallSessionDTO> { },
                CallType = "test",
                StartDateTime = DateTime.Now.AddMinutes(-5),
                EndDateTime = DateTime.Now,
            };
            call.Sessions.Add(new CallSessionDTO
            {
                Callee = new ParticipantEndpointDTO { Identity = new IdentitySetDTO { User = new UserDTO { Id = user1.Id } } },
                Caller = new ParticipantEndpointDTO { Identity = new IdentitySetDTO { User = new UserDTO { Id = user2.Id } } },
                CallType = "test",
                StartDateTime = DateTime.Now.AddMinutes(-5),
                EndDateTime = DateTime.Now,
                Modalities = new string[] { "this", "that" }
            });

            var r = Request.CreateResponse(HttpStatusCode.OK, call);

            return r;
        }


        public static string GetUrl(int skip, int maxCount, int pageSize)
        {
            return $"https://contoso.local/fakepagedresults?skip={skip}&maxCount={maxCount}&pageSize={pageSize}";
        }
    }
}
