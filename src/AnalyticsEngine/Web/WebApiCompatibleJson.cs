using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Web.AnalyticsWeb
{
    /// <summary>
    /// Makes every controller write (and read) JSON exactly as the Web API 2 / MVC 5 build on
    /// <c>dev</c> and <c>main</c> does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ASP.NET Core's default is System.Text.Json with a camelCase naming policy, and System.Text.Json
    /// ignores every Newtonsoft attribute the shared models carry. On this host that meant:
    /// </para>
    /// <list type="bullet">
    ///   <item>an explicit <c>[JsonProperty("manager")]</c> went out as <c>managerUserPrincipalName</c>
    ///   (the Copilot Adoption licensed-user, opportunity and Cowork tables), and
    ///   <c>[JsonProperty("available_credits")]</c> as <c>availableCredits</c> - fields the portal
    ///   reads by the names the attributes give them;</item>
    ///   <item><c>[JsonIgnore]</c>d members were published, internal ones among them - the licence
    ///   activity read-model key, the adoption summary's raw source warnings;</item>
    ///   <item>an unannotated property went out camelCased, where the Web API 2 build writes it exactly
    ///   as declared.</item>
    /// </list>
    /// <para>
    /// The portal is shared with <c>dev</c> and <c>main</c> and is written against their contract, so
    /// this host has to serialise as they do: Newtonsoft with Web API 2's defaults, i.e. a
    /// <see cref="DefaultContractResolver"/> with no naming strategy (names as declared unless
    /// <c>[JsonProperty]</c> or <c>[JsonObject(NamingStrategyType = ...)]</c> say otherwise),
    /// <see cref="MissingMemberHandling.Ignore"/> and <see cref="TypeNameHandling.None"/>. Only the
    /// contract resolver differs from <c>AddNewtonsoftJson</c>'s own defaults, which camelCase.
    /// </para>
    /// <para>
    /// Shared by <c>Program</c> and the in-memory test hosts, so a test through the pipeline sees the
    /// bytes production sends.
    /// </para>
    /// </remarks>
    public static class WebApiCompatibleJson
    {
        /// <summary>Replaces the System.Text.Json formatters with Newtonsoft configured as Web API 2 was.</summary>
        public static IMvcBuilder AddWebApiCompatibleJson(this IMvcBuilder builder)
        {
            return builder.AddNewtonsoftJson(options => Apply(options.SerializerSettings));
        }

        /// <summary>Applies Web API 2's JSON defaults to <paramref name="settings"/>.</summary>
        public static JsonSerializerSettings Apply(JsonSerializerSettings settings)
        {
            // AddNewtonsoftJson defaults to a camelCase naming strategy; Web API 2's formatter has none.
            settings.ContractResolver = new DefaultContractResolver();
            settings.MissingMemberHandling = MissingMemberHandling.Ignore;
            // Never anything else: honouring $type in a request body is a deserialisation gadget.
            settings.TypeNameHandling = TypeNameHandling.None;
            return settings;
        }
    }
}
