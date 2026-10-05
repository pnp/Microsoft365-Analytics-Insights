using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// An exception that carries the reply its request gets - the ASP.NET Core stand-in for Web API 2's
    /// <c>HttpResponseException</c>, which ASP.NET Core does not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The .NET Framework build on <c>dev</c>/<c>main</c> throws <c>HttpResponseException</c> from code that is not
    /// itself an action - <c>ReportScopeResolver</c> refusing a report whose global filter cannot be evaluated, the
    /// Copilot Adoption API refusing a date range it cannot read - and Web API 2 sends the response it carries from
    /// any controller. Here <see cref="ApiReplyExceptionFilterAttribute"/> does that, on every controller that can
    /// throw one; <c>GlobalFilterWebTests</c> fails for a controller that resolves a report scope without it.
    /// </para>
    /// <para>
    /// A controller that catches <see cref="Exception"/> rethrows this first, exactly where the .NET Framework build
    /// rethrows <c>HttpResponseException</c>, so a deliberate reply is never turned into a generic error.
    /// </para>
    /// </remarks>
    public sealed class ApiReplyException : Exception
    {
        public ApiReplyException(IActionResult result, string message) : base(message)
        {
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        /// <summary>The reply the request gets instead of whatever the action would have returned.</summary>
        public IActionResult Result { get; }
    }

    /// <summary>
    /// Sends an <see cref="ApiReplyException"/>'s reply rather than leaving it to the generic error handling.
    /// </summary>
    /// <remarks>
    /// The reply is marked <c>no-store, private</c>: it is a refusal that depends on who asked and on settings an
    /// administrator can change at any moment, which is how the .NET Framework build marks its own.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class ApiReplyExceptionFilterAttribute : ExceptionFilterAttribute
    {
        public override void OnException(ExceptionContext context)
        {
            if (context?.Exception is ApiReplyException reply)
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store, private";
                context.Result = reply.Result;
                context.ExceptionHandled = true;
            }
        }
    }
}
