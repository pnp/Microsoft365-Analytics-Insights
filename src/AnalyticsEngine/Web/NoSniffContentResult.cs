using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// A <see cref="ContentResult"/> that also sends <c>X-Content-Type-Options: nosniff</c>, so a browser never reads the body
    /// as anything but its declared content type.
    /// </summary>
    /// <remarks>
    /// net10: the anonymous endpoints' replies (#853) - the calls webhook's validation token, which is whatever the caller
    /// sent, and its 400, and <c>api/ImportConfig</c>'s 400 and 403 - which the Web API 2 build writes as
    /// <c>HttpResponseMessage</c>s carrying the header. Set as the result executes rather than on the controller's
    /// <c>Response</c>, so the action stays callable without an HTTP context, as the tests that call it directly do, and is
    /// still a <see cref="ContentResult"/> to them.
    /// </remarks>
    public sealed class NoSniffContentResult : ContentResult
    {
        internal const string HeaderName = "X-Content-Type-Options";

        public override Task ExecuteResultAsync(ActionContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            context.HttpContext.Response.Headers[HeaderName] = "nosniff";
            return base.ExecuteResultAsync(context);
        }
    }
}
