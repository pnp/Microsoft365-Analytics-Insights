using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Config;

namespace Web.Controllers
{
    [ApiController]
    [Route("api/auth")]
    [AllowAnonymous]
    public class AuthController : ControllerBase
    {
        private readonly WebAppConfig _configuration;

        public AuthController(WebAppConfig configuration)
        {
            _configuration = configuration;
        }

        [HttpGet("config")]
        public ActionResult<AuthClientConfig> GetConfig()
        {
            return Ok(new AuthClientConfig
            {
                Authority = _configuration.AzureAd.Authority,
                ClientId = _configuration.AzureAd.ClientId,
                Scope = _configuration.AzureAd.ApiScope
            });
        }
    }

    public sealed class AuthClientConfig
    {
        public string Authority { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string Scope { get; set; } = string.Empty;
    }
}
