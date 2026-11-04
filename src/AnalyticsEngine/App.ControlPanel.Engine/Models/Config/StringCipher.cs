using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Encrypts the secrets persisted in installer configuration files - the SQL admin password and
    /// the app registration client secrets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in the installer engine, its only user. It used to be in <c>DataUtils</c>, which every
    /// release package references, so BouncyCastle (about 2.4 MB compressed) was copied into the
    /// importer and web site packages that never encrypt anything.
    /// </para>
    /// <para>
    /// <b>The format is versioned, and every version ever written must stay readable.</b> A configuration
    /// file has to open with the same password that saved it, whichever build of the installer saved it.
    /// <see cref="Encrypt"/> always writes the current version, so a file saved by an older build is
    /// upgraded the next time it is saved. Every format detail below is pinned by
    /// <c>StringCipherCompatibilityTests</c> against golden vectors; if those fail, the change has broken
    /// config files in the field.
    /// </para>
    /// <para>
    /// <b>Version 2</b> (written since the November 2026 review): <c>v2:</c> followed by base64 of
    /// <c>[16 bytes salt][16 bytes IV][ciphertext][32 bytes tag]</c>. Key derivation: PBKDF2-HMAC-SHA256,
    /// 600,000 iterations, to a 256-bit master key; the encryption key and the authentication key are
    /// HMAC-SHA256 of the master key over <see cref="EncryptionKeyLabel"/> and <see cref="AuthenticationKeyLabel"/>.
    /// Cipher: AES-256, CBC, PKCS7. Tag: HMAC-SHA256 over <c>v2</c>, the salt, the IV and the ciphertext
    /// (encrypt-then-MAC), checked before anything is decrypted, so a wrong password or an altered value
    /// is always refused rather than decrypted to noise. The prefix contains a colon, which base64 never
    /// does, so a version 2 value can't be mistaken for a version 1 one.
    /// </para>
    /// <para>
    /// <b>Version 1</b> (read only): base64 of <c>[32 bytes salt][32 bytes IV][ciphertext]</c>.
    /// Key derivation: PBKDF2-HMAC-SHA1, 1000 iterations, 256-bit key. Cipher: <b>Rijndael with a
    /// 256-bit block</b>, CBC, PKCS7, with no integrity check. Its cheap key derivation is why version 2 exists.
    /// </para>
    /// <para>
    /// A 256-bit block means version 1 is Rijndael but <b>not AES</b> - AES is Rijndael fixed to a
    /// 128-bit block. The implementation previously used <c>RijndaelManaged</c>, which supports the
    /// larger block only on .NET Framework. On .NET Core / .NET 5+ that type is implemented over AES and
    /// throws <c>PlatformNotSupportedException: BlockSize must be 128 in this implementation.</c>, so the
    /// installer could neither read nor write a config file once ported. Since .NET has no in-box
    /// 256-bit-block Rijndael, the primitive comes from BouncyCastle's <see cref="RijndaelEngine"/>,
    /// which implements the full Rijndael specification and behaves identically on both runtimes.
    /// </para>
    /// <para>
    /// BouncyCastle is used for both versions on every target framework rather than conditionally, so
    /// byte-compatibility between builds is structural rather than something two implementations have to
    /// keep agreeing on.
    /// </para>
    /// <para>
    /// The 600,000-iteration derivation takes a second or two on .NET Framework. Each passphrase is derived
    /// once per process: a process encrypts every value with one random salt per passphrase (a fresh IV per
    /// value), and remembers the keys it has derived, so saving and reopening a file costs one derivation
    /// rather than one per secret. Reusing the salt within a process costs nothing in strength: a password
    /// guess has to be checked against one value anyway.
    /// </para>
    /// <para>
    /// An installer older than this format cannot open a value it writes, and reports it as a wrong password.
    /// </para>
    /// </remarks>
    public static class StringCipher
    {
        /// <summary>The marker every value written in the current format starts with.</summary>
        public const string CurrentFormatPrefix = "v2:";

        internal const int CurrentIterations = 600000;
        internal const string EncryptionKeyLabel = "StringCipher.v2.encryption";
        internal const string AuthenticationKeyLabel = "StringCipher.v2.authentication";

        private const int CurrentSaltBytes = 16;
        private const int CurrentIvBytes = 16;
        private const int CurrentTagBytes = 32;
        private const int CurrentKeyBytes = 32;
        private const int AesBlockBytes = 16;
        private const int MaxCachedKeys = 32;

        private static readonly object KeyCacheLock = new object();
        private static readonly byte[] CacheKeySecret = GenerateRandomBytes(32);
        private static readonly Dictionary<string, byte[]> DerivedKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, byte[]> ProcessSalts = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // Version 1: key size of the encryption algorithm in bits. Divided by 8 below for the byte count.
        private const int Keysize = 256;

        // Version 1: number of iterations for the password bytes generation function.
        private const int DerivationIterations = 1000;

        // Version 1: Rijndael block size in bits. This is NOT AES - see the remarks above.
        private const int BlockSizeBits = 256;

        private const int SaltBytes = Keysize / 8;
        private const int IvBytes = BlockSizeBits / 8;

        /// <summary>Encrypts <paramref name="plainText"/> in the current format.</summary>
        public static string Encrypt(string plainText, string passPhrase)
        {
            if (plainText == null) throw new ArgumentNullException(nameof(plainText));
            if (passPhrase == null) throw new ArgumentNullException(nameof(passPhrase));

            byte[] salt;
            var keys = CurrentKeys(passPhrase, null, out salt);
            var iv = GenerateRandomBytes(CurrentIvBytes);

            byte[] cipherTextBytes;
            try
            {
                cipherTextBytes = CreateAesCipher(true, keys.Encryption, iv).DoFinal(Encoding.UTF8.GetBytes(plainText));
            }
            catch (CryptoException ex)
            {
                throw new CryptographicException("Failed to encrypt the value.", ex);
            }

            var tag = Tag(keys.Authentication, salt, iv, cipherTextBytes);
            return CurrentFormatPrefix + Convert.ToBase64String(salt.Concat(iv).Concat(cipherTextBytes).Concat(tag).ToArray());
        }

        /// <summary>Decrypts a value written in any version of the format.</summary>
        /// <exception cref="CryptographicException">The password is wrong, or the value is damaged or not one of ours.</exception>
        public static string Decrypt(string cipherText, string passPhrase)
        {
            if (cipherText == null) throw new ArgumentNullException(nameof(cipherText));
            if (passPhrase == null) throw new ArgumentNullException(nameof(passPhrase));

            if (cipherText.StartsWith(CurrentFormatPrefix, StringComparison.Ordinal))
            {
                return DecryptCurrent(cipherText.Substring(CurrentFormatPrefix.Length), passPhrase);
            }

            if (cipherText.IndexOf(':') >= 0)
            {
                // Base64 never contains a colon, so this is a version marker this build doesn't know.
                throw new CryptographicException("The value was saved in a format this installer doesn't know. Open it with the installer that saved it, or a newer one.");
            }

            return DecryptVersion1(cipherText, passPhrase);
        }

        /// <summary>Whether <paramref name="cipherText"/> is already in the current format.</summary>
        public static bool IsCurrentFormat(string cipherText) =>
            cipherText != null && cipherText.StartsWith(CurrentFormatPrefix, StringComparison.Ordinal);

        /// <summary>Forgets every derived key and salt, as a new process would. For tests.</summary>
        internal static void ResetKeyCache()
        {
            lock (KeyCacheLock)
            {
                DerivedKeys.Clear();
                ProcessSalts.Clear();
            }
        }

        private static string DecryptCurrent(string payload, string passPhrase)
        {
            byte[] all;
            try
            {
                all = Convert.FromBase64String(payload);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("The encrypted value is not valid base64.", ex);
            }

            var cipherLength = all.Length - CurrentSaltBytes - CurrentIvBytes - CurrentTagBytes;
            if (cipherLength < AesBlockBytes || cipherLength % AesBlockBytes != 0)
                throw new CryptographicException("The encrypted value is not a whole number of blocks with a salt, an IV and a tag.");

            var salt = all.Take(CurrentSaltBytes).ToArray();
            var iv = all.Skip(CurrentSaltBytes).Take(CurrentIvBytes).ToArray();
            var cipherTextBytes = all.Skip(CurrentSaltBytes + CurrentIvBytes).Take(cipherLength).ToArray();
            var tag = all.Skip(CurrentSaltBytes + CurrentIvBytes + cipherLength).ToArray();

            byte[] unused;
            var keys = CurrentKeys(passPhrase, salt, out unused);
            if (!FixedTimeEquals(tag, Tag(keys.Authentication, salt, iv, cipherTextBytes)))
                throw new CryptographicException("Failed to decrypt the value. The password is incorrect, or the value has been altered.");

            try
            {
                return Encoding.UTF8.GetString(CreateAesCipher(false, keys.Encryption, iv).DoFinal(cipherTextBytes));
            }
            catch (CryptoException ex)
            {
                throw new CryptographicException("Failed to decrypt the value.", ex);
            }
        }

        private static string DecryptVersion1(string cipherText, string passPhrase)
        {
            byte[] cipherTextBytesWithSaltAndIv;
            try
            {
                cipherTextBytesWithSaltAndIv = Convert.FromBase64String(cipherText);
            }
            catch (FormatException ex)
            {
                // Callers treat a CryptographicException as "this did not decrypt"; a malformed
                // payload is the same outcome from their point of view.
                throw new CryptographicException("The encrypted value is not valid base64.", ex);
            }

            if (cipherTextBytesWithSaltAndIv.Length <= SaltBytes + IvBytes)
                throw new CryptographicException("The encrypted value is too short to contain a salt, an IV and a block.");

            // [32 bytes of Salt] + [32 bytes of IV] + [n bytes of CipherText]
            var saltStringBytes = cipherTextBytesWithSaltAndIv.Take(SaltBytes).ToArray();
            var ivStringBytes = cipherTextBytesWithSaltAndIv.Skip(SaltBytes).Take(IvBytes).ToArray();
            var cipherTextBytes = cipherTextBytesWithSaltAndIv.Skip(SaltBytes + IvBytes).ToArray();

            var keyBytes = DeriveKey(passPhrase, saltStringBytes);

            byte[] plainTextBytes;
            try
            {
                plainTextBytes = CreateCipher(false, keyBytes, ivStringBytes).DoFinal(cipherTextBytes);
            }
            catch (CryptoException ex)
            {
                // A wrong password fails PKCS7 padding validation. Callers such as
                // SolutionInstallConfig.LoadFromJson depend on CryptographicException to report
                // "wrong password" rather than crashing, so BouncyCastle's exception is translated.
                throw new CryptographicException("Failed to decrypt the value. The password is probably incorrect.", ex);
            }

            return Encoding.UTF8.GetString(plainTextBytes);
        }

        private sealed class CurrentKeySet
        {
            public byte[] Encryption;
            public byte[] Authentication;
        }

        /// <summary>
        /// The version 2 keys for <paramref name="passPhrase"/> and <paramref name="salt"/>, derived at most once per
        /// process. With no salt, the salt this process encrypts with for that passphrase (created on first use).
        /// </summary>
        private static CurrentKeySet CurrentKeys(string passPhrase, byte[] salt, out byte[] usedSalt)
        {
            var passKey = PassphraseCacheKey(passPhrase);
            byte[] master;
            lock (KeyCacheLock)
            {
                if (salt == null && !ProcessSalts.TryGetValue(passKey, out salt))
                {
                    if (ProcessSalts.Count >= MaxCachedKeys) ProcessSalts.Clear();
                    salt = GenerateRandomBytes(CurrentSaltBytes);
                    ProcessSalts[passKey] = salt;
                }
                DerivedKeys.TryGetValue(passKey + "|" + Convert.ToBase64String(salt), out master);
            }

            if (master == null)
            {
                // Outside the lock: a derivation takes a second or two, and two threads deriving the same key is harmless.
                var generator = new Pkcs5S2ParametersGenerator(new Sha256Digest());
                generator.Init(Encoding.UTF8.GetBytes(passPhrase), salt, CurrentIterations);
                master = ((KeyParameter)generator.GenerateDerivedMacParameters(CurrentKeyBytes * 8)).GetKey();
                lock (KeyCacheLock)
                {
                    if (DerivedKeys.Count >= MaxCachedKeys) DerivedKeys.Clear();
                    DerivedKeys[passKey + "|" + Convert.ToBase64String(salt)] = master;
                }
            }

            usedSalt = salt;
            return new CurrentKeySet
            {
                Encryption = HmacSha256(master, Encoding.ASCII.GetBytes(EncryptionKeyLabel)),
                Authentication = HmacSha256(master, Encoding.ASCII.GetBytes(AuthenticationKeyLabel)),
            };
        }

        /// <summary>A per-process keyed hash of the passphrase, so the cache never holds a fast hash of it.</summary>
        private static string PassphraseCacheKey(string passPhrase) =>
            Convert.ToBase64String(HmacSha256(CacheKeySecret, Encoding.UTF8.GetBytes(passPhrase)));

        private static byte[] Tag(byte[] authenticationKey, byte[] salt, byte[] iv, byte[] cipherText) =>
            HmacSha256(authenticationKey, Encoding.ASCII.GetBytes("v2").Concat(salt).Concat(iv).Concat(cipherText).ToArray());

        private static byte[] HmacSha256(byte[] key, byte[] data)
        {
            var hmac = new HMac(new Sha256Digest());
            hmac.Init(new KeyParameter(key));
            hmac.BlockUpdate(data, 0, data.Length);
            var output = new byte[hmac.GetMacSize()];
            hmac.DoFinal(output, 0);
            return output;
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            var difference = left.Length ^ right.Length;
            for (var i = 0; i < left.Length && i < right.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static PaddedBufferedBlockCipher CreateAesCipher(bool forEncryption, byte[] key, byte[] iv)
        {
            var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new AesEngine()), new Pkcs7Padding());
            cipher.Init(forEncryption, new ParametersWithIV(new KeyParameter(key), iv));
            return cipher;
        }

        /// <summary>
        /// Version 1: PBKDF2-HMAC-SHA1 over the UTF8 bytes of the passphrase. SHA-1 is not a free choice here:
        /// it is what <c>Rfc2898DeriveBytes</c> used by default when the format was established, so every
        /// version 1 config file depends on it.
        /// </summary>
        private static byte[] DeriveKey(string passPhrase, byte[] salt)
        {
            var generator = new Pkcs5S2ParametersGenerator(new Sha1Digest());
            generator.Init(Encoding.UTF8.GetBytes(passPhrase), salt, DerivationIterations);
            return ((KeyParameter)generator.GenerateDerivedParameters("AES", Keysize)).GetKey();
        }

        private static PaddedBufferedBlockCipher CreateCipher(bool forEncryption, byte[] key, byte[] iv)
        {
            var cipher = new PaddedBufferedBlockCipher(
                new CbcBlockCipher(new RijndaelEngine(BlockSizeBits)),
                new Pkcs7Padding());
            cipher.Init(forEncryption, new ParametersWithIV(new KeyParameter(key), iv));
            return cipher;
        }

        private static byte[] GenerateRandomBytes(int count)
        {
            var randomBytes = new byte[count];
            // Cryptographically secure; BouncyCastle's SecureRandom defers to the platform RNG.
            new SecureRandom().NextBytes(randomBytes);
            return randomBytes;
        }
    }
}
