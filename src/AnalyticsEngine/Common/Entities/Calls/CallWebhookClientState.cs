using Common.Entities.Config;
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Common.Entities.Calls
{
    /// <summary>
    /// The <c>clientState</c> of the Teams calls webhook's Graph subscription: the value the importer creates the
    /// subscription with, and the check the anonymous webhook applies to the copy Graph echoes in every notification.
    /// Shared so the importer and the web app derive exactly the same value from the same app settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be the runtime app registration's client secret itself, so the secret travelled in every
    /// notification Graph sent and was stored in every message queued - or dead-lettered - from one. It is now
    /// <c>base64url(HMAC-SHA256(key: the secret, message: "callrecords-clientstate/v1|tenant|client"))</c>: one-way,
    /// so it never reveals the secret; deterministic, so the importer and the web app agree with nothing new to
    /// configure; and keyed by the secret, so it changes when the secret is rotated (the importer notices that from the
    /// fingerprint it records, and replaces the subscription - see <c>CallWebhook</c>).
    /// </para>
    /// <para>
    /// 43 characters, unpadded base64url. Graph caps <c>clientState</c> at 128 characters and documents no character
    /// restrictions; base64url is only ASCII letters, digits, '-' and '_', which pass through JSON and URLs unchanged.
    /// </para>
    /// <para>
    /// With no client secret (certificate-only authentication) there is nothing to derive from, and nothing weaker is
    /// substituted: <see cref="IsAvailable"/> is false, no subscription is created, and every notification is refused.
    /// The subscription is created with a client-secret credential anyway, so that mode has never had a working calls
    /// webhook.
    /// </para>
    /// </remarks>
    public sealed class CallWebhookClientState
    {
        /// <summary>
        /// Versioned so the derivation can change: a new version changes every value, and with it the fingerprint the
        /// importer compares, so every deployment's subscription is replaced on its next cycle.
        /// </summary>
        public const string PurposeV1 = "callrecords-clientstate/v1";

        /// <summary>No client secret is configured.</summary>
        public static readonly CallWebhookClientState Unavailable = new CallWebhookClientState(null, null);

        // SHA-256 digests, so a check compares two fixed-length values in constant time and never the strings.
        private readonly byte[] _valueDigest;
        private readonly byte[] _legacySecretDigest;

        private CallWebhookClientState(string value, string legacySecret)
        {
            Value = value;
            if (value != null)
            {
                _valueDigest = Sha256(value);
                _legacySecretDigest = Sha256(legacySecret);
                Fingerprint = ToHex(_valueDigest);
            }
        }

        /// <summary>The clientState for this deployment's app settings.</summary>
        public static CallWebhookClientState ForConfig(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return For(config.ClientSecret, config.TenantGUID.ToString(), config.ClientID);
        }

        /// <summary>The clientState for an app registration; <see cref="Unavailable"/> without a client secret.</summary>
        public static CallWebhookClientState For(string clientSecret, string tenantId, string clientId)
        {
            return string.IsNullOrEmpty(clientSecret)
                ? Unavailable
                : new CallWebhookClientState(Derive(clientSecret, tenantId, clientId), clientSecret);
        }

        /// <summary>False when no client secret is configured: then no notification is ever accepted.</summary>
        public bool IsAvailable => Value != null;

        /// <summary>
        /// The value to create the subscription with. A credential for the webhook: never log it.
        /// </summary>
        public string Value { get; }

        /// <summary>
        /// Lower-case hex SHA-256 of <see cref="Value"/>. What the importer stores to notice that the value changed;
        /// it cannot be turned back into the value.
        /// </summary>
        public string Fingerprint { get; }

        /// <summary>
        /// Checks a notification's <c>clientState</c>: <see cref="CallWebhookClientStateMatch.Current"/> for the derived
        /// value, <see cref="CallWebhookClientStateMatch.LegacySecret"/> for the raw client secret that subscriptions
        /// created before the change still carry, otherwise <see cref="CallWebhookClientStateMatch.NoMatch"/>. Exact
        /// and ordinal - a case-insensitive or trimmed comparison would weaken it - and constant-time.
        /// </summary>
        public CallWebhookClientStateMatch Match(string presented)
        {
            if (presented == null || _valueDigest == null) return CallWebhookClientStateMatch.NoMatch;

            var digest = Sha256(presented);
            var isCurrent = FixedTimeEquals(digest, _valueDigest);
            var isLegacy = FixedTimeEquals(digest, _legacySecretDigest);

            if (isCurrent) return CallWebhookClientStateMatch.Current;
            return isLegacy ? CallWebhookClientStateMatch.LegacySecret : CallWebhookClientStateMatch.NoMatch;
        }

        /// <summary>
        /// Whether <paramref name="presented"/> is the clientState whose <see cref="Fingerprint"/> is
        /// <paramref name="fingerprintHex"/> - how the web app recognises the clientState of the client secret in use before
        /// a rotation, which it no longer has, from the fingerprint the importer recorded. Exact, ordinal and constant-time,
        /// like <see cref="Match"/>; false for anything that is not a 64-character hex fingerprint.
        /// </summary>
        public static bool MatchesFingerprint(string presented, string fingerprintHex)
        {
            if (string.IsNullOrEmpty(presented)) return false;
            var expected = FromHex(fingerprintHex);
            return expected != null && FixedTimeEquals(Sha256(presented), expected);
        }

        private static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length != 64) return null;
            var bytes = new byte[32];
            for (var i = 0; i < 32; i++)
            {
                var hi = HexValue(hex[2 * i]);
                var lo = HexValue(hex[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                bytes[i] = (byte)((hi << 4) | lo);
            }
            return bytes;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>
        /// The v1 derivation. The tenant and client ids are GUIDs, so they are normalised to lower case: two processes
        /// reading differently-cased settings must still agree.
        /// </summary>
        public static string Derive(string clientSecret, string tenantId, string clientId)
        {
            if (string.IsNullOrEmpty(clientSecret)) throw new ArgumentException("A client secret is required.", nameof(clientSecret));

            var message = PurposeV1 + "|" + Normalise(tenantId) + "|" + Normalise(clientId);
            var key = Encoding.UTF8.GetBytes(clientSecret);

            // Can only repeat for a secret of a few base64url characters, which a 43-character output might happen to
            // contain. Deterministic, so both processes still agree; real secrets never get past the first attempt.
            for (var attempt = 0; attempt < 64; attempt++)
            {
                var input = attempt == 0 ? message : message + "|" + attempt.ToString(CultureInfo.InvariantCulture);
                string candidate;
                using (var hmac = new HMACSHA256(key))
                {
                    candidate = Base64UrlNoPadding(hmac.ComputeHash(Encoding.UTF8.GetBytes(input)));
                }

                if (candidate.IndexOf(clientSecret, StringComparison.Ordinal) < 0) return candidate;
            }

            throw new InvalidOperationException("Could not derive a webhook clientState that does not contain the client secret.");
        }

        private static string Normalise(string id) => (id ?? string.Empty).Trim().ToLowerInvariant();

        private static string Base64UrlNoPadding(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Sha256(string value)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            }
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < a.Length && i < b.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }
    }

    /// <summary>The result of <see cref="CallWebhookClientState.Match"/>.</summary>
    public enum CallWebhookClientStateMatch
    {
        NoMatch,

        /// <summary>The derived value: a subscription the importer created after the change.</summary>
        Current,

        /// <summary>The raw client secret: a subscription created before the change, not yet replaced.</summary>
        LegacySecret
    }
}
