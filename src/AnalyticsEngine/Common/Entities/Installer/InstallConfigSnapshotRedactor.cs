using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Common.Entities.Installer
{
    /// <summary>
    /// Removes secrets from the installer configuration snapshots kept in <c>sys_configs.config_json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The installer records a snapshot of its configuration with every install, and the portal's Install log shows
    /// it to holders of Portal.Administration. Earlier installers wrote the encrypted SQL administrator password
    /// (<c>SQLServerAdminPasswordHash</c>) and both app registration secrets (<c>SecretHash</c>) into it. Despite
    /// the names these are cipher texts under the operator's config-file password, so whoever can read them can
    /// guess that password offline. A snapshot is a record of what was configured, not a store of credentials.
    /// </para>
    /// <para>
    /// The rule is broader than those three properties: any property, at any depth, whose name marks a secret, and
    /// any string that is a connection string or URL carrying a credential. A secret-bearing setting added later is
    /// then withheld without anyone having to remember this class.
    /// </para>
    /// </remarks>
    public static class InstallConfigSnapshotRedactor
    {
        // A credential inside a value: "Password=" / "Pwd=" in a SQL connection string, "AccountKey=" in a storage
        // one, "SharedAccessKey=" in a Service Bus one, and the signature of a SAS URL.
        static readonly Regex CredentialInValue = new Regex(
            @"(^|[;?&])\s*(password|pwd|accountkey|sharedaccesskey|sharedaccesssignature|client_?secret|sig)\s*=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Whether a property with this name holds a secret, or a cipher text of one.</summary>
        public static bool IsSecretPropertyName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            return name.EndsWith("Hash", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Key", StringComparison.OrdinalIgnoreCase)
                || Contains(name, "Secret")
                || Contains(name, "Password")
                || Contains(name, "Passwd")
                || Contains(name, "Pwd")
                || Contains(name, "Token");
        }

        /// <summary>Whether a value is a connection string or URL that carries a credential.</summary>
        public static bool CarriesCredential(string value) => !string.IsNullOrEmpty(value) && CredentialInValue.IsMatch(value);

        /// <summary>
        /// <paramref name="json"/> without its secrets; null when it is not JSON. Text that cannot be parsed is
        /// withheld whole, never passed through, because nothing can be removed from it selectively.
        /// </summary>
        public static string Redact(string json) => Redact(json, out _);

        /// <inheritdoc cref="Redact(string)"/>
        /// <param name="removedAny">True when at least one secret was removed.</param>
        public static string Redact(string json, out bool removedAny)
        {
            removedAny = false;
            if (string.IsNullOrWhiteSpace(json)) return json;

            JToken token;
            try
            {
                token = Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }

            removedAny = RemoveSecrets(token);
            return token.ToString(Formatting.None);
        }

        /// <summary>Removes secrets from <paramref name="token"/> in place, recursively.</summary>
        /// <returns>True when anything was removed.</returns>
        public static bool RemoveSecrets(JToken token)
        {
            var removed = false;
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (IsSecretPropertyName(property.Name) || IsCredentialString(property.Value))
                    {
                        property.Remove();
                        removed = true;
                    }
                    else
                    {
                        removed |= RemoveSecrets(property.Value);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array.ToList())
                {
                    if (IsCredentialString(item))
                    {
                        item.Remove();
                        removed = true;
                    }
                    else
                    {
                        removed |= RemoveSecrets(item);
                    }
                }
            }
            return removed;
        }

        /// <summary>
        /// Removes secrets from the snapshots that earlier installers stored in <c>sys_configs</c>.
        /// </summary>
        /// <remarks>
        /// Idempotent and cheap: the table holds one row per install, only rows that mention a secret the installer
        /// has ever written are read, and a row is rewritten only when something is removed, so once it has run this
        /// reads nothing. Rows that are not JSON are left as they are; the Install log withholds them anyway.
        /// </remarks>
        /// <returns>How many rows were rewritten.</returns>
        public static int ScrubStoredSnapshots(Database database)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));

            var candidates = database.SqlQuery<StoredSnapshot>(
                "SELECT id AS Id, config_json AS ConfigJson FROM dbo.sys_configs " +
                "WHERE config_json LIKE N'%Hash%' OR config_json LIKE N'%Secret%' OR config_json LIKE N'%Password%'")
                .ToList();

            var rewritten = 0;
            foreach (var row in candidates)
            {
                var redacted = Redact(row.ConfigJson, out var removedAny);
                if (redacted == null || !removedAny) continue;

                database.ExecuteSqlCommand("UPDATE dbo.sys_configs SET config_json = @p0 WHERE id = @p1", redacted, row.Id);
                rewritten++;
            }
            return rewritten;
        }

        static bool IsCredentialString(JToken token) => token.Type == JTokenType.String && CarriesCredential((string)token);

        static bool Contains(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        // Dates and decimals are kept as written: the snapshot is shown as it was recorded, not as this parser reads it.
        static JToken Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal,
            })
            {
                var token = JToken.ReadFrom(reader);
                if (reader.Read() && reader.TokenType != JsonToken.Comment)
                {
                    throw new JsonReaderException("Unexpected content after the configuration JSON.");
                }
                return token;
            }
        }

        /// <summary>One stored snapshot. Public only so EF can materialise it.</summary>
        public sealed class StoredSnapshot
        {
            public int Id { get; set; }
            public string ConfigJson { get; set; }
        }
    }
}
