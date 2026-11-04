using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Web.Config;

namespace Web.Startup
{
    /// <summary>
    /// What one anonymous upload (<c>POST /api/Telemetry</c>) may cost: how big it may be, and how many one
    /// address may send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The upload endpoint has to stay anonymous, because importers have no Entra identity. The secret that signs
    /// uploads ships inside every importer package, so a valid signature shows that a payload was built by importer
    /// code, not which install sent it or whether it is genuine. Each accepted upload costs a Cosmos read and two
    /// writes, and adds a document to the history container. These limits are what bound that cost.
    /// </para>
    /// <para>
    /// <b>Body size.</b> An importer sends one report a day: a row per table in its database (a couple of hundred,
    /// about 100 bytes each) and, when adoption metrics are enabled, a block of aggregates (about 500 bytes per
    /// licence SKU). That is a few tens of KB: about 40 KB for 200 tables and 15 SKUs.
    /// <c>UploadLimitsTests.LargestRealisticPayload_FitsWellWithinTheLimit</c> builds one with 1,000 tables and
    /// 100 SKUs, far beyond any real deployment: about 180 KB, under a fifth of <see cref="MaxRequestBodyBytes"/>,
    /// and the test fails if it ever reaches a quarter. The limit also keeps every stored document well inside
    /// Cosmos DB's 2 MB item limit. Without it, ASP.NET Core's default of about 30 MB applied.
    /// </para>
    /// <para>
    /// <b>Rate.</b> Each IP address gets <see cref="WebAppConfig.GetMaxUploadsPerAddressPerHour"/> requests an hour
    /// (30 unless <c>MaxUploadsPerAddressPerHour</c> is set). Every request counts, accepted or not, so the limit
    /// also bounds signature checks. An importer uploads once a day after a successful upload, and on the next
    /// import cycle after a failed one, so even several installs sharing an outbound address stay far below it.
    /// A refused importer gets 429, logs it as a failed upload and sends a fresh report on a later cycle; nothing
    /// is lost. IPv6 addresses are grouped by /64, the block one subscriber is normally given, otherwise the limit
    /// would be per address in a block of 2^64.
    /// </para>
    /// </remarks>
    public static class UploadLimits
    {
        /// <summary>Rate limiter policy for the upload endpoint.</summary>
        public const string RateLimitPolicy = "TelemetryUpload";

        /// <summary>Largest request body the upload endpoint reads: 1 MiB.</summary>
        public const long MaxRequestBodyBytes = 1024 * 1024;

        /// <summary>Length of the fixed rate-limit window.</summary>
        public static readonly TimeSpan RateLimitWindow = TimeSpan.FromHours(1);

        /// <summary>Partition for a request whose remote address the server cannot tell.</summary>
        internal const string UnknownAddressPartition = "(unknown address)";

        /// <summary>
        /// Registers the upload endpoint's rate limiter. <c>app.UseRateLimiter()</c> applies it, and
        /// <c>[EnableRateLimiting(RateLimitPolicy)]</c> on the action opts the endpoint in; nothing else is limited.
        /// </summary>
        public static IServiceCollection AddUploadRateLimiting(this IServiceCollection services)
        {
            services.AddRateLimiter(options =>
            {
                // The default is 503, which reads as an outage rather than "slow down".
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.OnRejected = (context, _) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    return ValueTask.CompletedTask;
                };
            });

            // Configured once the container exists, so the limit comes from the same WebAppConfig every other
            // setting does rather than from a second parse of the configuration.
            services.AddOptions<RateLimiterOptions>()
                .Configure<WebAppConfig>((options, config) =>
                {
                    var permitsPerWindow = config.GetMaxUploadsPerAddressPerHour();

                    options.AddPolicy(RateLimitPolicy, httpContext =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            PartitionKeyFor(httpContext.Connection.RemoteIpAddress),
                            _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = permitsPerWindow,
                                Window = RateLimitWindow,

                                // Refuse at once. A queued upload would hold a request open for up to an hour.
                                QueueLimit = 0,
                                AutoReplenishment = true,
                            }));
                });

            return services;
        }

        /// <summary>The address a request is counted against.</summary>
        internal static string PartitionKeyFor(IPAddress? address)
        {
            if (address is null)
            {
                return UnknownAddressPartition;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = address.GetAddressBytes();
                Array.Clear(bytes, 8, 8);
                return new IPAddress(bytes) + "/64";
            }

            return address.ToString();
        }
    }
}
