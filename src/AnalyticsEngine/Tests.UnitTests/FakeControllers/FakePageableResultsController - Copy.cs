using System.Net;
using System.Net.Http;
using System.Web.Http;
using Tests.UnitTests.FakeEntities;

namespace Tests.UnitTests.FakeControllers
{
    public class FakePageableResultsController : ApiController
    {
        public FakePageableResultsController()
        {
        }

        [HttpGet]
        [Route("fakepagedresults")]
        public HttpResponseMessage PageTest(int skip, int maxCount, int pageSize)
        {
            var data = new FakePagedResult();
            var to = skip + pageSize;
            if (to > maxCount)
            {
                to = maxCount;
            }

            for (int i = skip; i < to; i++)
            {
                data.PageResults.Add(new FakeResult { Index = i });
            }
            if (to < maxCount)
            {
                data.OdataNextLink = GetUrl(skip + pageSize, maxCount, pageSize);
            }

            var r = Request.CreateResponse(HttpStatusCode.OK, data);


            return r;
        }


        public static string GetUrl(int skip, int maxCount, int pageSize)
        {
            return $"https://contoso.local/fakepagedresults?skip={skip}&maxCount={maxCount}&pageSize={pageSize}";
        }
    }
}
