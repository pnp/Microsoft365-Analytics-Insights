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
    /// <summary>
    /// A synthetic Teams group call whose sessions span two Graph pages, as Graph returns any call with more than 60:
    /// the call record carries the first 60 sessions inline plus a <c>sessions@odata.nextLink</c>, and that link returns
    /// the rest. The attendee <see cref="LateAttendeeUpn"/> is only on the second page. Contoso names and zeroed GUIDs;
    /// each instance gets its own ids so database tests never collide.
    /// </summary>
    public sealed class SyntheticMultiPageCall
    {
        public const string TenantId = "00000000-0000-0000-0000-000000000000";

        private static readonly DateTime CallStartUtc = new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

        private readonly string _tail;
        private readonly List<Person> _attendees = new List<Person>();

        public SyntheticMultiPageCall(int firstPageAttendees = 60, int secondPageAttendees = 1, bool documentedNextLinkShape = false)
        {
            if (firstPageAttendees < 1 || secondPageAttendees < 1) throw new ArgumentOutOfRangeException(nameof(firstPageAttendees));

            _tail = Guid.NewGuid().ToString("N").Substring(0, 12);
            CallId = $"00000000-0000-0000-ca11-{_tail}";
            Organiser = new Person(0, $"organiser.{_tail}@contoso.com", "Contoso Organiser", _tail);
            for (var i = 1; i <= firstPageAttendees + secondPageAttendees; i++)
            {
                var isLate = i == firstPageAttendees + secondPageAttendees;
                _attendees.Add(isLate
                    ? new Person(i, $"late.attendee.{_tail}@contoso.com", "Contoso Late Attendee", _tail)
                    : new Person(i, $"attendee{i:000}.{_tail}@contoso.com", $"Contoso Attendee {i:000}", _tail));
            }
            FirstPageAttendees = firstPageAttendees;

            CallRecordUrl = $"https://graph.microsoft.com/v1.0/communications/callRecords/{CallId}?$expand=sessions($expand=segments)";
            SecondPageUrl = $"https://graph.microsoft.com/v1.0/communications/callRecords('{CallId}')/sessions?$expand=segments&$skiptoken=page2-{_tail}";

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
                ["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#communications/callRecords(sessions(segments()))/$entity",
                ["version"] = 1,
                ["type"] = "groupCall",
                ["modalities"] = new JArray("audio", "video"),
                ["lastModifiedDateTime"] = CallStartUtc.AddHours(2),
                ["startDateTime"] = CallStartUtc,
                ["endDateTime"] = CallStartUtc.AddHours(1),
                ["id"] = CallId,
                ["organizer"] = new JObject { ["user"] = Organiser.UserJson() },
                ["sessions@odata.context"] = $"https://graph.microsoft.com/v1.0/$metadata#communications/callRecords('{CallId}')/sessions(segments())",
                ["sessions"] = new JArray(_attendees.Take(FirstPageAttendees).Select(SessionJson)),
                ["sessions@odata.nextLink"] = SecondPageLinkAsReturned
            };
            return call.ToString();
        }

        /// <param name="nextLink">A further continuation link, to script runaway paging; none by default.</param>
        public string SecondPageJson(string nextLink = null)
        {
            var page = new JObject
            {
                ["@odata.context"] = $"https://graph.microsoft.com/v1.0/$metadata#communications/callRecords('{CallId}')/sessions(segments())",
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
            graph.Serve(CallRecordUrl, CallRecordJson());
            graph.Serve(SecondPageUrl, SecondPageJson());
            foreach (var person in new[] { Organiser }.Concat(_attendees))
            {
                graph.Users[person.Id] = new JObject { ["id"] = person.Id, ["userPrincipalName"] = person.Upn }.ToString();
            }
            return graph;
        }

        /// <summary>The production source loader, reading from <paramref name="graph"/>.</summary>
        public static GraphCallRecordSourceLoader CreateSourceLoader(SyntheticCallGraphHandler graph)
        {
            var graphClient = new GraphServiceClient(new HttpClient(graph, disposeHandler: false), new BearerTokenAuthenticationProvider("test-token"));
            return new GraphCallRecordSourceLoader(new ManualGraphCallClient(graph, NullLogger.Instance), new TeamsLoadContext(graphClient), NullLogger.Instance, TenantId);
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
                ["callee"] = Endpoint(attendee),
                ["segments"] = new JArray(new JObject
                {
                    ["id"] = $"00000000-0000-0002-{attendee.Index:x4}-{_tail}",
                    ["startDateTime"] = start,
                    ["endDateTime"] = start.AddMinutes(25)
                })
            };
        }

        private static JObject Endpoint(Person person)
        {
            return new JObject
            {
                ["@odata.type"] = "#microsoft.graph.callRecords.participantEndpoint",
                ["userAgent"] = new JObject
                {
                    ["@odata.type"] = "#microsoft.graph.callRecords.clientUserAgent",
                    ["headerValue"] = "Contoso-Synthetic/1.0 (Teams)",
                    ["platform"] = "windows",
                    ["productFamily"] = "teams"
                },
                ["identity"] = new JObject
                {
                    ["@odata.type"] = "#microsoft.graph.identitySet",
                    ["user"] = person.UserJson()
                }
            };
        }

        public sealed class Person
        {
            public Person(int index, string upn, string displayName, string tail)
            {
                Index = index;
                Upn = upn;
                DisplayName = displayName;
                Id = $"00000000-0000-0000-{index:x4}-{tail}";
            }

            public int Index { get; }
            public string Id { get; }
            public string Upn { get; }
            public string DisplayName { get; }

            public JObject UserJson() => new JObject { ["id"] = Id, ["displayName"] = DisplayName, ["tenantId"] = TenantId };
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

        public List<string> Requests { get; } = new List<string>();

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
