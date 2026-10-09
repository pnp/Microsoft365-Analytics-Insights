using Newtonsoft.Json;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Common.Entities.State
{
    /// <summary>Authenticated encryption shared by the portal and importer, not tied to one Windows machine.</summary>
    public sealed class AgentCostTokenProtection
    {
        private const string Purpose = "AnalyticsState.AgentCostDelegatedAuth.v1";
        private readonly string _secret;
        private readonly X509Certificate2 _certificate;

        public AgentCostTokenProtection(string secret, X509Certificate2 certificate = null)
        {
            if (certificate == null && string.IsNullOrWhiteSpace(secret))
                throw new ArgumentException("A runtime credential is required for token protection.");
            _secret = secret;
            _certificate = certificate;
        }

        public string Protect(byte[] plaintext)
        {
            var keys = KeyMaterial();
            using (var aes = Aes.Create())
            {
                aes.Key = keys.Take(32).ToArray();
                aes.GenerateIV();
                byte[] ciphertext;
                using (var encryptor = aes.CreateEncryptor())
                    ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);

                var envelope = new Envelope
                {
                    Version = 1,
                    WrappedKey = _certificate == null ? null : Wrap(keys),
                    IV = Convert.ToBase64String(aes.IV),
                    Ciphertext = Convert.ToBase64String(ciphertext),
                };
                using (var hmac = new HMACSHA256(keys.Skip(32).ToArray()))
                    envelope.Tag = Convert.ToBase64String(hmac.ComputeHash(AuthenticatedBytes(envelope)));
                return JsonConvert.SerializeObject(envelope);
            }
        }

        public byte[] Unprotect(string value)
        {
            var envelope = JsonConvert.DeserializeObject<Envelope>(value);
            if (envelope?.Version != 1 || (_certificate == null) != (envelope.WrappedKey == null))
                throw new CryptographicException("The delegated cache requires reconnection.");
            var keys = envelope.WrappedKey == null ? KeyMaterial() : Unwrap(envelope.WrappedKey);
            using (var hmac = new HMACSHA256(keys.Skip(32).ToArray()))
            {
                var actual = hmac.ComputeHash(AuthenticatedBytes(envelope));
                var expected = Convert.FromBase64String(envelope.Tag);
                var mismatch = actual.Length ^ expected.Length;
                for (var i = 0; i < actual.Length && i < expected.Length; i++)
                    mismatch |= actual[i] ^ expected[i];
                if (mismatch != 0) throw new CryptographicException("The delegated cache requires reconnection.");
            }
            using (var aes = Aes.Create())
            {
                aes.Key = keys.Take(32).ToArray();
                aes.IV = Convert.FromBase64String(envelope.IV);
                var bytes = Convert.FromBase64String(envelope.Ciphertext);
                using (var decryptor = aes.CreateDecryptor())
                    return decryptor.TransformFinalBlock(bytes, 0, bytes.Length);
            }
        }

        private byte[] KeyMaterial()
        {
            if (_certificate != null)
            {
                var keys = new byte[64];
                using (var random = RandomNumberGenerator.Create()) random.GetBytes(keys);
                return keys;
            }
            using (var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_secret)))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(Purpose));
        }

        private string Wrap(byte[] keys)
        {
            using (var rsa = _certificate.GetRSAPublicKey())
                return Convert.ToBase64String(rsa.Encrypt(keys, RSAEncryptionPadding.OaepSHA1));
        }

        private byte[] Unwrap(string keys)
        {
            using (var rsa = _certificate.GetRSAPrivateKey())
                return rsa.Decrypt(Convert.FromBase64String(keys), RSAEncryptionPadding.OaepSHA1);
        }

        private static byte[] AuthenticatedBytes(Envelope envelope) =>
            Encoding.UTF8.GetBytes(Purpose + "|" + envelope.WrappedKey + "|" + envelope.IV + "|" + envelope.Ciphertext);

        private sealed class Envelope
        {
            public int Version { get; set; }
            public string WrappedKey { get; set; }
            public string IV { get; set; }
            public string Ciphertext { get; set; }
            public string Tag { get; set; }
        }
    }
}
