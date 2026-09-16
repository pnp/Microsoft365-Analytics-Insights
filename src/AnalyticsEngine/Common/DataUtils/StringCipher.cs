using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace DataUtils
{
    /// <summary>
    /// Encrypts the secrets persisted in installer configuration files - the SQL admin password and
    /// the app registration client secrets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The on-disk format is fixed and must never change.</b> A configuration file has to open with
    /// the same password that saved it, whichever build of the installer is opening it. Every format
    /// detail below is pinned by <c>StringCipherCompatibilityTests</c> against golden vectors captured
    /// from a shipping build; if those fail, the change has broken every config file in the field.
    /// </para>
    /// <para>
    /// Format: <c>[32 bytes salt][32 bytes IV][ciphertext]</c>, base64-encoded.
    /// Key derivation: PBKDF2-HMAC-SHA1, 1000 iterations, 256-bit key.
    /// Cipher: <b>Rijndael with a 256-bit block</b>, CBC, PKCS7.
    /// </para>
    /// <para>
    /// Note that a 256-bit block means this is Rijndael but <b>not AES</b> - AES is Rijndael fixed to a
    /// 128-bit block. The implementation previously used <c>RijndaelManaged</c>, which supports the
    /// larger block only on .NET Framework. On .NET Core / .NET 5+ that type is implemented over AES and
    /// throws <c>PlatformNotSupportedException: BlockSize must be 128 in this implementation.</c>, so the
    /// installer could neither read nor write a config file once ported. Since .NET has no in-box
    /// 256-bit-block Rijndael, the primitive comes from BouncyCastle's <see cref="RijndaelEngine"/>,
    /// which implements the full Rijndael specification and behaves identically on both runtimes.
    /// </para>
    /// <para>
    /// BouncyCastle is used on every target framework rather than conditionally, so byte-compatibility
    /// between builds is structural rather than something two implementations have to keep agreeing on.
    /// </para>
    /// </remarks>
    public static class StringCipher
    {
        // Key size of the encryption algorithm in bits. Divided by 8 below for the byte count.
        private const int Keysize = 256;

        // Number of iterations for the password bytes generation function.
        private const int DerivationIterations = 1000;

        // Rijndael block size in bits. This is NOT AES - see the remarks above.
        private const int BlockSizeBits = 256;

        private const int SaltBytes = Keysize / 8;
        private const int IvBytes = BlockSizeBits / 8;

        public static string Encrypt(string plainText, string passPhrase)
        {
            if (plainText == null) throw new ArgumentNullException(nameof(plainText));
            if (passPhrase == null) throw new ArgumentNullException(nameof(passPhrase));

            // Salt and IV are randomly generated each time and prepended to the cipher text, so the
            // same salt and IV are available when decrypting.
            var saltStringBytes = GenerateRandomBytes(SaltBytes);
            var ivStringBytes = GenerateRandomBytes(IvBytes);
            var keyBytes = DeriveKey(passPhrase, saltStringBytes);

            byte[] cipherTextBytes;
            try
            {
                cipherTextBytes = CreateCipher(true, keyBytes, ivStringBytes)
                    .DoFinal(Encoding.UTF8.GetBytes(plainText));
            }
            catch (CryptoException ex)
            {
                throw new CryptographicException("Failed to encrypt the value.", ex);
            }

            return Convert.ToBase64String(
                saltStringBytes.Concat(ivStringBytes).Concat(cipherTextBytes).ToArray());
        }

        public static string Decrypt(string cipherText, string passPhrase)
        {
            if (cipherText == null) throw new ArgumentNullException(nameof(cipherText));
            if (passPhrase == null) throw new ArgumentNullException(nameof(passPhrase));

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

        /// <summary>
        /// PBKDF2-HMAC-SHA1 over the UTF8 bytes of the passphrase. SHA-1 is not a free choice here: it
        /// is what <c>Rfc2898DeriveBytes</c> used by default when the format was established, so every
        /// existing config file depends on it.
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
