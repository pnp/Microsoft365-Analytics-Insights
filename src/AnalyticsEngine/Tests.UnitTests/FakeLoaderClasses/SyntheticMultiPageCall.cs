using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>Which of Graph's two schemas the synthetic payload speaks.</summary>
    public enum CallPayloadShape
    {
        /// <summary>The retired <c>organizer</c> and endpoint <c>identity</c> only (what Graph returned until 30 June 2026).</summary>
        Legacy,

        /// <summary><c>organizer_v2</c> and endpoint <c>associatedIdentity</c> only (what Graph returns once the legacy fields stop).</summary>
        V2,

        /// <summary>Both, as Graph returned while the two overlapped.</summary>
        Both,

        /// <summary>Endpoints in the v2 shape and no organiser identity in either schema.</summary>
        NoOrganiser
    }

    /// <summary>
    /// A synthetic Teams group call whose sessions span two Graph pages, as Graph returns any call with more than 60:
    /// the call record carries the first 60 sessions inline plus a <c>sessions@odata.nextLink</c>, and that link returns
    /// the rest. The attendee <see cref="LateAttendeeUpn"/> is only on the second page. Contoso names and zeroed GUIDs;
    /// each instance gets its own ids so database tests never collide.
    /// </summary>
    public sealed class SyntheticMultiPageCall
    {
        public const string TenantId = "00000000-0000-0000-0000-000000000000";
        public const string OtherTenantId = "00000000-0000-0000-0000-000000000001";

        private static readonly DateTime CallStartUtc = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _tail;
        private readonly List<Person> _attendees = new List<Person>();
        private readonly CallPayloadShape _shape;

        public SyntheticMultiPageCall(int firstPageAttendees = 60, int secondPageAttendees = 1, bool documentedNextLinkShape = false,
            CallPayloadShape shape = CallPayloadShape.Legacy, string organiserTenantId = TenantId)
        {
            if (firstPageAttendees < 1 || secondPageAttendees < 1) throw new ArgumentOutOfRangeException(nameof(firstPageAttendees));

            _shape = shape;
            _tail = Guid.NewGuid().ToString("N").Substring(0, 12);
            CallId = $"00000000-0000-0000-ca11-{_tail}";
            Organiser = new Person(0, $"organiser.{_tail}@contoso.com", "Contoso Organiser", _tail, organiserTenantId);
            for (var i = 1; i <= firstPageAttendees + secondPageAttendees; i++)
            {
                var isLate = i == firstPageAttendees + secondPageAttendees;
                _attendees.Add(isLate
                    ? new Person(i, $"late.attendee.{_tail}@contoso.com", "Contoso Late Attendee", _tail)
                    : new Person(i, $"attendee{i:000}.{_tail}@contoso.com", $"Contoso Attendee {i:000}", _tail));
            }
            FirstPageAttendees = firstPageAttendees;

            CallRecordUrl = $"https://graph.microsoft.com/v1.0/communications/callRecords/{CallId}?$expand=sessions";
            SecondPageUrl = $"https://graph.microsoft.com/v1.0/communications/callRecords('{CallId}')/sessions?$skiptoken=page2-{_tail}";

            // The shape Graph's own documentation shows, which cannot be requested as it stands.
            SecondPageLinkAsReturned = documentedNextLinkShape
                ? SecondPageUrl.Replace("/v1.0/", "/v1.0/$metadata#")
                : SecondPageUrl;
        }

        public string CallId { get; }
        public Person Organiser { get; }
        public int FirstPageAttendees { get; }
        public int TotalSessions => _attendees.Count;
        public IReadOnlyList<Person> Attendees => _attendees;
        public string LateAttendeeUpn => _attendees.Last().Upn;

        /// <summary>What the importer requests for the call record itself.</summary>
        public string CallRecordUrl { get; }

        /// <summary>Where the second page of sessions is really served.</summary>
        public string SecondPageUrl { get; }

        /// <summary>The <c>sessions@odata.nextLink</c> value in the call record.</summary>
        public string SecondPageLinkAsReturned { get; }

        public string CallRecordJson()
        {
            var call = new JObject
            {
                ["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#communications/callRecords(sessions())/$entity",
                ["version"] = 1,
                ["type"] = "groupCall",
                ["modalities"] = new JArray("audio", "video"),
                ["lastModifiedDateTime"] = CallStartUtc.AddHours(2),
                ["startDateTime"] = CallStartUtc,
                ["endDateTime"] = CallStartUtc.AddHours(1),
                ["id"] = CallId,
                ["sessions@odata.context"] = $"https://graph.microsoft.com/v1.0/$metadata#communications/callRecords('{CallId}')/sessions",
                ["sessions"] = new JArray(_attendees.Take(FirstPageAttendees).Select(SessionJson)),
                ["sessions@odata.nextLink"] = SecondPageLinkAsReturned
            };
            if (_shape == CallPayloadShape.Legacy || _shape == CallPayloadShape.Both)
            {
                call["organizer"] = new JObject { ["user"] = Organiser.UserJson() };
            }
            if (_shape == CallPayloadShape.V2 || _shape == CallPayloadShape.Both)
            {
                call["organizer_v2"] = new JObject { ["id"] = Organiser.Id, ["identity"] = new JObject { ["user"] = Organiser.UserJson() } };
            }
            return call.ToString();
        }

        /// <param name="nextLink">A further continuation link, to script runaway paging; none by default.</param>
        public string SecondPageJson(string nextLink = null)
        {
            var page = new JObject
            {
                ["@odata.context"] = $"https://graph.microsoft.com/v1.0/$metadata#communications/callRecords('{CallId}')/sessions",
                ["value"] = new JArray(_attendees.Skip(FirstPageAttendees).Select(SessionJson))
            };
            if (nextLink != null)
            {
                page["@odata.nextLink"] = nextLink;
            }
            return page.ToString();
        }

        /// <summary>Graph, answering for this call: the call record, its second sessions page, and user look-ups.</summary>
        public SyntheticCallGraphHandler CreateGraph()
        {
            var graph = new SyntheticCallGraphHandler();
            ServeOn(graph);
            return graph;
        }

        /// <summary>Makes <paramref name="graph"/> answer for this call too, so one Graph can serve several calls.</summary>
        public void ServeOn(SyntheticCallGraphHandler graph)
        {
            graph.Serve(CallRecordUrl, CallRecordJson());
            graph.Serve(SecondPageUrl, SecondPageJson());
            foreach (var person in new[] { Organiser }.Concat(_attendees))
            {
                graph.Users[person.Id] = new JObject { ["id"] = person.Id, ["userPrincipalName"] = person.Upn }.ToString();
            }
        }

        /// <summary>The production source loader, reading from <paramref name="graph"/>.</summary>
        public static GraphCallRecordSourceLoader CreateSourceLoader(SyntheticCallGraphHandler graph, TeamsLoadContext context = null)
        {
            return new GraphCallRecordSourceLoader(new ManualGraphCallClient(graph, NullLogger.Instance), context ?? CreateLoadContext(graph), NullLogger.Instance, TenantId);
        }

        /// <summary>A Teams load context whose Graph client reads from <paramref name="graph"/>.</summary>
        public static TeamsLoadContext CreateLoadContext(SyntheticCallGraphHandler graph)
        {
            var graphClient = new GraphServiceClient(new HttpClient(graph, disposeHandler: false), new BearerTokenAuthenticationProvider("test-token"));
            return new TeamsLoadContext(graphClient);
        }

        private JObject SessionJson(Person attendee)
        {
            var start = CallStartUtc.AddMinutes(attendee.Index % 30);
            return new JObject
            {
                ["id"] = $"00000000-0000-0001-{attendee.Index:x4}-{_tail}",
                ["modalities"] = new JArray("audio"),
                ["startDateTime"] = start,
                ["endDateTime"] = start.AddMinutes(25),
                ["isTest"] = false,
                ["caller"] = Endpoint(Organiser),
                ["callee"] = Endpoint(attendee)
            };
        }

        private JObject Endpoint(Person person)
        {
            var endpoint = new JObject
            {
                ["@odata.type"] = "#microsoft.graph.callRecords.participantEndpoint",
                ["userAgent"] = new JObject
                {
                    ["@odata.type"] = "#microsoft.graph.callRecords.clientUserAgent",
                    ["headerValue"] = "Contoso-Synthetic/1.0 (Teams)",
                    ["platform"] = "windows",
                    ["productFamily"] = "teams"
                }
            };
            if (_shape == CallPayloadShape.Legacy || _shape == CallPayloadShape.Both)
            {
                endpoint["identity"] = new JObject
                {
                    ["@odata.type"] = "#microsoft.graph.identitySet",
                    ["user"] = person.UserJson()
                };
            }
            if (_shape != CallPayloadShape.Legacy)
            {
                endpoint["associatedIdentity"] = person.UserJson();
            }
            return endpoint;
        }

        public sealed class Person
        {
            public Person(int index, string upn, string displayName, string tail, string tenantId = TenantId)
            {
                Index = index;
                Upn = upn;
                DisplayName = displayName;
                Id = $"00000000-0000-0000-{index:x4}-{tail}";
                PersonTenantId = tenantId;
            }

            public int Index { get; }
            public string Id { get; }
            public string Upn { get; }
            public string DisplayName { get; }
            public string PersonTenantId { get; }

            public JObject UserJson() => new JObject { ["id"] = Id, ["displayName"] = DisplayName, ["tenantId"] = PersonTenantId };
        }
    }

    /// <summary>
    /// Fake Graph for <see cref="SyntheticMultiPageCall"/>: canned responses by exact URL, user look-ups by id, and a
    /// record of every request.
    /// </summary>
    public sealed class SyntheticCallGraphHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Tuple<HttpStatusCode, string>> _responses = new Dictionary<string, Tuple<HttpStatusCode, string>>(StringComparer.Ordinal);

        public Dictionary<string, string> Users { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Answers any request with no canned response, when it returns JSON; for scripting endless paging.</summary>
        public Func<Uri, string> Generate { get; set; }

        /// <summary>Answers every user look-up with this instead of the user, e.g. a gateway's HTML 502.</summary>
        public Func<HttpResponseMessage> FailUserLookups { get; set; }

        public List<string> Requests { get; } = new List<string>();

        /// <summary>The user look-ups made, in order.</summary>
        public List<string> UserRequests
        {
            get { lock (Requests) { return Requests.Where(r => r.IndexOf("/v1.0/users/", StringComparison.OrdinalIgnoreCase) >= 0).ToList(); } }
        }
        /// <summary>The requests made for the call record and its sessions, in order (user look-ups left out).</summary>
        public List<string> CallRecordRequests
        {
            get { lock (Requests) { return Requests.Where(r => r.IndexOf("/communications/", StringComparison.OrdinalIgnoreCase) >= 0).ToList(); } }
        }

        public void Serve(string url, string json) => _responses[Normalise(url)] = Tuple.Create(HttpStatusCode.OK, json);

        public void Fail(string url, HttpStatusCode status) =>
            _responses[Normalise(url)] = Tuple.Create(status, $"{{\"error\":{{\"code\":\"{status}\",\"message\":\"Simulated failure.\"}}}}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri.AbsoluteUri;
            lock (Requests)
            {
                Requests.Add(url);
            }

            if (FailUserLookups != null && request.RequestUri.AbsolutePath.StartsWith("/v1.0/users/", StringComparison.OrdinalIgnoreCase))
            {
                var failure = FailUserLookups();
                failure.RequestMessage = request;
                return Task.FromResult(failure);
            }

            Tuple<HttpStatusCode, string> response;
            if (!_responses.TryGetValue(url, out response))
            {
                const string usersPath = "/v1.0/users/";
                var path = request.RequestUri.AbsolutePath;
                string userJson = null;
                var generated = Generate?.Invoke(request.RequestUri);
                if (generated != null)
                {
                    response = Tuple.Create(HttpStatusCode.OK, generated);
                }
                else if (path.StartsWith(usersPath, StringComparison.OrdinalIgnoreCase) &&
                    Users.TryGetValue(Uri.UnescapeDataString(path.Substring(usersPath.Length)), out userJson))
                {
                    response = Tuple.Create(HttpStatusCode.OK, userJson);
                }
                else
                {
                    response = Tuple.Create(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Not found.\"}}");
                }
            }

            return Task.FromResult(new HttpResponseMessage(response.Item1)
            {
                Content = new StringContent(response.Item2, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }

        private static string Normalise(string url) => new Uri(url, UriKind.Absolute).AbsoluteUri;
    }
}
