using Azure;
using System;

namespace CloudInstallEngine.Azure
{
    /// <summary>Why Azure Key Vault rejected a data-plane call with HTTP 403.</summary>
    public enum KeyVaultForbiddenReason
    {
        /// <summary>Could not be determined from the response.</summary>
        Unknown,

        /// <summary>
        /// The caller was refused by networking: the vault firewall did not list the caller's address,
        /// public network access is switched off, or the call did not arrive over an approved private link.
        /// </summary>
        NetworkBlocked,

        /// <summary>The caller reached the vault but lacks the access policy / RBAC permission.</summary>
        PermissionDenied,
    }

    /// <summary>
    /// Works out which kind of 403 Key Vault returned, so the installer can tell an operator whether to
    /// fix networking or fix permissions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both causes surface identically at the top level - the Azure SDK reports
    /// <see cref="RequestFailedException.ErrorCode"/> as <c>Forbidden</c> for each - and the installer used
    /// to log only that code, discarding the message. The two need opposite remedies, so an operator was
    /// left to guess, and the installer's own guidance listed both possibilities without choosing. The
    /// distinguishing detail is in the message text (and Key Vault's inner error code), which is what this
    /// classifies.
    /// </para>
    /// <para>
    /// Matching is on message text because the SDK does not surface Key Vault's <c>innererror</c> code as a
    /// property. Order matters: a firewall rejection says "Client address is not authorized", which would
    /// also match a naive "not authorized" permission test, so the networking markers are checked first.
    /// </para>
    /// <para>
    /// See Microsoft's <see href="https://learn.microsoft.com/en-us/azure/key-vault/general/common-error-codes">
    /// common error codes for Azure Key Vault</see>.
    /// </para>
    /// </remarks>
    public static class KeyVaultForbiddenClassifier
    {
        // Key Vault's inner error codes, plus the human-readable text that accompanies them. Kept as
        // fragments rather than whole messages because the message also carries caller/vault details.
        static readonly string[] NetworkMarkers =
        {
            "forbiddenbyfirewall",
            "forbiddenbyconnection",
            "client address is not authorized",
            "client address isn't authorized",
            "public network access is disabled",
            "public access is disabled",
            "approved private link",
            "does not allow access to",
        };

        static readonly string[] PermissionMarkers =
        {
            "forbiddenbypolicy",
            "does not have secrets",
            "does not have keys",
            "does not have certificates",
            "is not authorized to perform action",
        };

        /// <summary>
        /// Classifies a Key Vault 403 from its message. Returns <see cref="KeyVaultForbiddenReason.Unknown"/>
        /// when the text matches nothing known, so callers can fall back to their existing diagnosis.
        /// </summary>
        public static KeyVaultForbiddenReason Classify(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return KeyVaultForbiddenReason.Unknown;

            // Networking first: a firewall rejection's wording overlaps the permission wording.
            if (ContainsAny(message, NetworkMarkers)) return KeyVaultForbiddenReason.NetworkBlocked;
            if (ContainsAny(message, PermissionMarkers)) return KeyVaultForbiddenReason.PermissionDenied;

            return KeyVaultForbiddenReason.Unknown;
        }

        /// <summary>Classifies the 403 carried by a <see cref="RequestFailedException"/>.</summary>
        public static KeyVaultForbiddenReason Classify(RequestFailedException ex)
        {
            return ex == null ? KeyVaultForbiddenReason.Unknown : Classify(ex.Message);
        }

        /// <summary>
        /// The first line of a Key Vault error message. The full message repeats the caller identity, vault
        /// name and request id over several lines, which buries the one sentence that explains the refusal.
        /// </summary>
        public static string FirstLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;

            var trimmed = message.Trim();
            var breakAt = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return breakAt < 0 ? trimmed : trimmed.Substring(0, breakAt).Trim();
        }

        static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (var needle in needles)
            {
                if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
