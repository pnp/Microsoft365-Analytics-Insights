namespace Web.Auth
{
    /// <summary>
    /// The exact shape of the signature an importer sends with each upload, checked before BCrypt runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bcrypt hash names its own cost factor, and <c>BCrypt.Verify</c> recomputes the hash at that cost: 2^cost
    /// rounds of key setup, for any cost up to 31. The upload endpoint is anonymous, so passing the caller's hash
    /// straight to BCrypt would let the caller decide how much CPU one request takes - minutes or hours of it for a
    /// high cost - before anything has been authenticated. Rate limiting cannot help with that: it runs before the
    /// request, not during it.
    /// </para>
    /// <para>
    /// Importers sign with <c>StringUtils.GetHashedStringWithSalt</c>, which calls BCrypt.Net-Next's
    /// <c>HashPassword(input, 4)</c>. That has always produced <c>$2a$04$</c> followed by 53 characters of salt and
    /// hash, so a signature of any other shape cannot have come from an importer, and is refused without hashing.
    /// <c>SignatureFromTheImporterCode_HasTheShapeTheServerAccepts</c> in the tests pins the two together.
    /// </para>
    /// </remarks>
    public static class UploadSignature
    {
        /// <summary>The bcrypt cost importers sign with: 2^4 rounds, about a millisecond to verify.</summary>
        public const int WorkFactor = 4;

        /// <summary>Bcrypt revision "2a", which BCrypt.Net-Next writes by default, then the cost.</summary>
        public const string Prefix = "$2a$04$";

        /// <summary>The prefix, a 22-character salt and a 31-character hash.</summary>
        public const int Length = 60;

        /// <summary>
        /// True only for a cost-4 bcrypt hash in the form importers send. Says nothing about whether it matches:
        /// that is still for BCrypt to decide, now at a cost the server chose.
        /// </summary>
        public static bool IsWellFormed(string? signature)
        {
            if (signature is null
                || signature.Length != Length
                || !signature.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            for (var i = Prefix.Length; i < signature.Length; i++)
            {
                if (!IsBcryptBase64(signature[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // Bcrypt's own base64 alphabet, which is not the RFC 4648 one: '.' where that has '+', and no padding.
        private static bool IsBcryptBase64(char c) =>
            c == '.' || c == '/'
            || (c >= 'A' && c <= 'Z')
            || (c >= 'a' && c <= 'z')
            || (c >= '0' && c <= '9');
    }
}
