using App.ControlPanel.Engine;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Tests.UnitTests
{
    /// <summary>
    /// Locks the on-disk format of installer configuration secrets (issue #520).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StringCipher"/> protects the two secrets persisted in every customer's installer
    /// config file - the SQL admin password and the app registration client secret. A config file
    /// MUST open with the same password that saved it, regardless of which build of the installer
    /// is doing the opening, and regardless of which .NET runtime that build targets.
    /// </para>
    /// <para>
    /// Compatibility has two directions and both are tested:
    /// </para>
    /// <list type="bullet">
    /// <item><b>old -> new</b>: the golden vectors. Stored ciphertext constants produced by the
    /// shipping .NET Framework 4.8 build, which the current implementation must still decrypt.
    /// These are deliberately captured constants rather than values the test encrypts first,
    /// because an encrypt-then-decrypt round trip passes on any implementation that is
    /// <i>consistently</i> wrong.</item>
    /// <item><b>new -> old</b>: <see cref="LegacyFormatOracle"/>. An independent reimplementation of
    /// the original format using only BCL primitives, which must be able to read whatever
    /// <see cref="StringCipher.Encrypt"/> writes today. Without this, an implementation that reads
    /// the legacy format but <i>writes</i> a new one would leave every golden vector green while
    /// producing files the shipping installer cannot open.</item>
    /// </list>
    /// <para>
    /// <b>Do not regenerate the golden vectors.</b> If a change makes them fail, the change has
    /// broken backwards compatibility with every config file in the field - fix the code, not the
    /// vectors.
    /// </para>
    /// <para>
    /// Format: <c>[32 bytes salt][32 bytes IV][ciphertext]</c>, base64.
    /// KDF: PBKDF2-HMAC-SHA1, 1000 iterations, 256-bit key.
    /// Cipher: Rijndael with a <b>256-bit block</b> (not AES, which is Rijndael fixed to 128), CBC, PKCS7.
    /// </para>
    /// <para>All values here are synthetic - no real customer password, secret or tenant.</para>
    /// </remarks>
    [TestClass]
    public class StringCipherCompatibilityTests
    {
        const string PassPhrase = "CorrectHorseBattery";

        const int SaltBytes = 32;
        const int IvBytes = 32;
        const int BlockBytes = 32;

        // -----------------------------------------------------------------------------------------
        // Golden vectors - captured from the shipping .NET Framework 4.8.9310.0 build.
        // -----------------------------------------------------------------------------------------

        const string GoldenAsciiPlain = "P@ssw0rd-SQLAdmin-2026";
        const string GoldenAsciiCipher =
            "Nn28dzFKcLT28jZG0JYNNOoOmvBM8eDHryOuSNW1ueESPiIFXAnU4qPUbRub9ccyRBkUkDa2u0P2vLi/M+JEDa/RLygUYs5B1raAC4yMDG72sDLbkn9WGGFCGhVCbMJd";

        // Non-Latin plaintext: the secret is UTF8-encoded before encryption, so a build that changed
        // the text encoding would still round-trip but would fail against this stored vector.
        const string GoldenGreekPlain = "Καλημέρα κόσμε";
        const string GoldenGreekCipher =
            "hVzuxJ0yeVlfLOVOyNLkTjjmU76NLFTYnVPa/dKgpmNQJU1yXAHMcOaOMJpLzsa9Rk+cENb11qcJcSzNenVN8N+Gae8ZJEZzwwQv/HW46CuD6a7y16/WRGpQaeoG0N6g";

        // An empty secret still produces a full block of PKCS7 padding.
        const string GoldenEmptyPlain = "";
        const string GoldenEmptyCipher =
            "J/raZfODQl3CcKMMtz5vHfWe/mhYKrw+v0S41dDrr3diD3TPd+9CgymlSaQel2f18FIiGj01Hfxa6HmHNTPlJlYYHEBHBO+HJx5iC7NKP/eVr82/C54UHjWQ6ewX9CmJ";

        // Characters that routinely appear in generated client secrets and SQL passwords.
        const string GoldenSpecialPlain = "a;b=c'd\"e\\f/g&h%i#j";
        const string GoldenSpecialCipher =
            "igDwVxE10ecrhtchCpRPScLxPeqSF5az1pb4RXyp/qTga79OVJaTRv9VbVS35GrQ/NtWRfHsxiscs/Yq/4hEcE2/eHTWB4HzngR7XsVAbjGenUNI9uiqEUKtAoVOPX6g";

        // A non-ASCII passphrase exercises the UTF8 encoding of the password inside the KDF.
        const string GoldenUnicodePass = "Κωδικός-Πρόσβασης";
        const string GoldenUnicodePassPlain = "SecretValue-123";
        const string GoldenUnicodePassCipher =
            "8fUmGwT0FsorqXvHsilEnIIEZXwJaa2Bx/akeVtAk9lntHbTRE7kJTi00NVoqjprpm1MFZYclt3/y5ssKMOMVbkE1R2G+NMtQzGVlG+uNEAxin6NSnlYrtZGFX7Jpf1G";

        // Exactly one block (32 UTF8 bytes). PKCS7 must append a WHOLE extra block of padding, so the
        // payload is 64 bytes. An implementation that skipped the extra block for aligned non-empty
        // input would corrupt secrets of exactly this length while every other vector stayed green.
        const string GoldenBlock32Plain = "0123456789abcdef0123456789abcdef";
        const string GoldenBlock32Cipher =
            "o1omh7TXxoDrI1bRGlrpuwxdMYg3npgmMQW1veOCl+kuPqRYQP1vJU7coYFswYPb06j00rq6oliN1oyH59InqvLTI2xGJ82CQJeCOlwI2aShRKbR+BThl8tsNJPP3eyqFE4Ktp+4ypc821ox+/leEuYMpYdP0HQpUV79L9h25c0=";

        // Exactly two blocks (64 UTF8 bytes), same reasoning across a multi-block payload.
        const string GoldenBlock64Plain = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string GoldenBlock64Cipher =
            "j0Q9W41TPX9c/pIR2V2HHsNoxVZAxm7f1H5+ZQbmMrXOb9t5lT5gqMUGU9FufODBG2w4OX30w5gby/eOQ4vymLpQb9yQCkjhSTR8vSopwECem90d1CNBjmVPG2jSjDRaYZ6Y3bHl3v93G9oyR5EurnGsw7kBd9oEimMFoHyFh7kureqf2hL/4xnajOjktxrJNnM44GJbvF3Yn7Tsu2pZ6A==";

        // 500 characters - spans many CBC blocks.
        static readonly string GoldenLongPlain = new string('X', 500);
        const string GoldenLongCipher =
            "OSN54QnqLZF4suJIHpvYgQVSKAqQ5kp36m0ELSvp0tOi8uoM9dH6jcf2lrN+KtvAArxMVDa7tASjqMfTSoDrP1ww6qHjkQ1Z3Wa9FsCQyzTg97Y7s5xikT5IAk6v1Kb4rpO2Qu0PQ5/LEnUVO+Km5xBQvgSms5XSpcDNPDZGNCujnqWRhDsh4swCGWs7HqwbOJ7R8j/lnAY8fQ2YIIoRIddM+ww0qCAaw/CokfAcYYZfB2evQKtyO8AXzXKWCu7/O5dpJpNEbE3CCmXu8pmFKQhZSsrDIXLkRBSWklSfP0+Y83nk16u/9hp7RIxqw97cxhriw3SDxt5G2qnsz5cEKG9m7MizltSeoEDbHTluDS8FaxqZ/J5vEzkC1rNkHCCigza65nFE53E4/HARv2olj8atpHbgiRz+23fYv4gAwMl96NoOMGw/GE0SVcI8YOkBQl0/v7/MNtNDpp8HzUkwP5u1KFJKXNTlmz1dBDG2g9NeTN7wRbd+BQ8cedKmHp/BI6sgo01i7ykGsz0k180ZhHE5I/g1nuD27EpvCyl30LDJqahOF+yH2RdH5mbed/to3PB697MEkyAqQKCknnUAPhVk+MIM+hc3M0k15z4kzMfUq5Ash9cbU8Ojb76MGqfo6mP7T2wJsm6a6tWyg1fShZI688oCbowpet2HSG2Wd4vctMNMqWwsiz5FIVRK0KAWmBABZ5n61wDeRYSrWc4XhQjacaqX6xrDBQjN+i1bTEAyhimc8PwGjCuFGdkoP8Ab";

        static IEnumerable<object[]> AllRepresentativeSecrets => new[]
        {
            new object[] { GoldenAsciiPlain, PassPhrase },
            new object[] { GoldenGreekPlain, PassPhrase },
            new object[] { GoldenEmptyPlain, PassPhrase },
            new object[] { GoldenSpecialPlain, PassPhrase },
            new object[] { GoldenBlock32Plain, PassPhrase },
            new object[] { GoldenBlock64Plain, PassPhrase },
            new object[] { GoldenLongPlain, PassPhrase },
            new object[] { "a", PassPhrase },
            // A non-ASCII passphrase must be covered on the WRITE path too, not just when decrypting a
            // stored vector. The passphrase is UTF8-encoded inside the KDF, so a writer that changed that
            // conversion would still round-trip in-process and still read every stored vector, while
            // producing files the shipping build cannot open.
            new object[] { GoldenUnicodePassPlain, GoldenUnicodePass },
            new object[] { GoldenGreekPlain, GoldenUnicodePass },
        };

        #region old -> new : files already on customer disks must still open

        [DataTestMethod]
        [DataRow(GoldenAsciiCipher, PassPhrase, GoldenAsciiPlain, DisplayName = "ASCII secret")]
        [DataRow(GoldenGreekCipher, PassPhrase, GoldenGreekPlain, DisplayName = "Non-Latin secret")]
        [DataRow(GoldenEmptyCipher, PassPhrase, GoldenEmptyPlain, DisplayName = "Empty secret")]
        [DataRow(GoldenSpecialCipher, PassPhrase, GoldenSpecialPlain, DisplayName = "Special characters")]
        [DataRow(GoldenUnicodePassCipher, GoldenUnicodePass, GoldenUnicodePassPlain, DisplayName = "Non-Latin passphrase")]
        [DataRow(GoldenBlock32Cipher, PassPhrase, GoldenBlock32Plain, DisplayName = "Exactly one block (32 bytes)")]
        [DataRow(GoldenBlock64Cipher, PassPhrase, GoldenBlock64Plain, DisplayName = "Exactly two blocks (64 bytes)")]
        public void Decrypt_CiphertextFromShippingNetFrameworkBuild_StillOpens(
            string cipherText, string passPhrase, string expectedPlainText)
        {
            var actual = StringCipher.Decrypt(cipherText, passPhrase);

            Assert.AreEqual(expectedPlainText, actual,
                "A configuration file saved by a previous build no longer decrypts. This breaks every " +
                "existing customer config - the encryption format must not change.");
        }

        [TestMethod]
        public void Decrypt_MultiBlockCiphertextFromShippingNetFrameworkBuild_StillOpens()
        {
            var actual = StringCipher.Decrypt(GoldenLongCipher, PassPhrase);

            Assert.AreEqual(GoldenLongPlain, actual,
                "Multi-block decryption regressed. A CBC chaining or block-size change can be invisible " +
                "on a single-block secret but corrupt a longer one.");
        }

        /// <summary>
        /// Fixture validation only: confirms the stored vectors themselves carry a whole extra block for
        /// block-aligned plaintext.
        /// </summary>
        /// <remarks>
        /// This deliberately does NOT call <see cref="StringCipher"/> - it is arithmetic over constants,
        /// and would stay green if today's writer stopped appending the full padding block. Writer
        /// behaviour for these same aligned inputs is covered by
        /// <see cref="Encrypt_OutputIsReadableByAnIndependentLegacyDecoder"/>, whose data includes both
        /// the 32-byte and 64-byte cases.
        /// </remarks>
        [DataTestMethod]
        [DataRow(GoldenEmptyPlain, GoldenEmptyCipher, 0, DisplayName = "0 bytes -> 1 padding block")]
        [DataRow(GoldenBlock32Plain, GoldenBlock32Cipher, 32, DisplayName = "32 bytes -> 2 blocks")]
        [DataRow(GoldenBlock64Plain, GoldenBlock64Cipher, 64, DisplayName = "64 bytes -> 3 blocks")]
        public void GoldenVectors_BlockAlignedPlainText_CarriesAFullPaddingBlock(
            string plainText, string cipherText, int expectedUtf8Length)
        {
            Assert.AreEqual(expectedUtf8Length, Encoding.UTF8.GetByteCount(plainText),
                "Fixture error: the plaintext is not the length this test assumes.");

            var payload = Convert.FromBase64String(cipherText).Length - SaltBytes - IvBytes;

            Assert.AreEqual(expectedUtf8Length + BlockBytes, payload,
                "A block-aligned plaintext must be followed by a whole extra block of PKCS7 padding.");
        }

        #endregion

        #region new -> old : files this build writes must open in the shipping build

        /// <summary>
        /// The critical direction that golden vectors alone cannot cover.
        /// </summary>
        /// <remarks>
        /// Decrypts fresh <see cref="StringCipher.Encrypt"/> output using <see cref="LegacyFormatOracle"/>,
        /// an independent reimplementation of the original format. Without this, an implementation that
        /// kept legacy decryption as a fallback but wrote a new format would pass every other test here
        /// while producing config files the currently-shipping installer cannot open.
        /// </remarks>
        [DataTestMethod]
        [DynamicData(nameof(AllRepresentativeSecrets))]
        public void Encrypt_OutputIsReadableByAnIndependentLegacyDecoder(string secret, string passPhrase)
        {
            if (!LegacyFormatOracle.IsAvailable)
                Assert.Inconclusive(LegacyFormatOracle.UnavailableReason);

            var cipherText = StringCipher.Encrypt(secret, passPhrase);

            var decodedByLegacyReader = LegacyFormatOracle.Decrypt(cipherText, passPhrase);

            Assert.AreEqual(secret, decodedByLegacyReader,
                "Ciphertext written by this build could not be read by an independent implementation of " +
                "the original format. A config file saved here would not open in the shipping installer.");
        }

        /// <summary>
        /// The oracle must itself be correct, or the test above proves nothing. Verify it against the
        /// stored vectors, which were produced by the real shipping build.
        /// </summary>
        [TestMethod]
        public void LegacyFormatOracle_ReadsTheStoredGoldenVectors()
        {
            if (!LegacyFormatOracle.IsAvailable)
                Assert.Inconclusive(LegacyFormatOracle.UnavailableReason);

            Assert.AreEqual(GoldenAsciiPlain, LegacyFormatOracle.Decrypt(GoldenAsciiCipher, PassPhrase));
            Assert.AreEqual(GoldenGreekPlain, LegacyFormatOracle.Decrypt(GoldenGreekCipher, PassPhrase));
            Assert.AreEqual(GoldenEmptyPlain, LegacyFormatOracle.Decrypt(GoldenEmptyCipher, PassPhrase));
            Assert.AreEqual(GoldenBlock32Plain, LegacyFormatOracle.Decrypt(GoldenBlock32Cipher, PassPhrase));
            Assert.AreEqual(GoldenLongPlain, LegacyFormatOracle.Decrypt(GoldenLongCipher, PassPhrase));
            Assert.AreEqual(GoldenUnicodePassPlain, LegacyFormatOracle.Decrypt(GoldenUnicodePassCipher, GoldenUnicodePass));
        }

        #endregion

        #region Format invariants

        [DataTestMethod]
        [DataRow(GoldenAsciiCipher, DisplayName = "ASCII secret")]
        [DataRow(GoldenGreekCipher, DisplayName = "Non-Latin secret")]
        [DataRow(GoldenEmptyCipher, DisplayName = "Empty secret")]
        [DataRow(GoldenSpecialCipher, DisplayName = "Special characters")]
        [DataRow(GoldenUnicodePassCipher, DisplayName = "Non-Latin passphrase")]
        [DataRow(GoldenBlock32Cipher, DisplayName = "Exactly one block")]
        [DataRow(GoldenBlock64Cipher, DisplayName = "Exactly two blocks")]
        [DataRow(GoldenLongCipher, DisplayName = "Multi-block secret")]
        public void GoldenVectors_HaveTheExpected256BitBlockLayout(string cipherText)
        {
            var raw = Convert.FromBase64String(cipherText);

            Assert.IsTrue(raw.Length > SaltBytes + IvBytes,
                "Ciphertext is too short to contain a salt, an IV and at least one block.");

            var payloadLength = raw.Length - SaltBytes - IvBytes;
            Assert.AreEqual(0, payloadLength % BlockBytes,
                $"Encrypted payload is {payloadLength} bytes, which is not a whole number of 32-byte " +
                "Rijndael-256 blocks. The cipher is Rijndael with a 256-bit block, NOT AES (16-byte block).");
        }

        /// <summary>
        /// Salt and IV must each be random per call. Comparing whole ciphertexts would pass if only one
        /// of the two varied, so the fields are compared independently.
        /// </summary>
        [TestMethod]
        public void Encrypt_RandomisesSaltAndIvIndependentlyOnEveryCall()
        {
            const int Samples = 8;
            var salts = new HashSet<string>();
            var ivs = new HashSet<string>();

            for (var i = 0; i < Samples; i++)
            {
                var raw = Convert.FromBase64String(StringCipher.Encrypt(GoldenAsciiPlain, PassPhrase));
                salts.Add(Convert.ToBase64String(raw.Take(SaltBytes).ToArray()));
                ivs.Add(Convert.ToBase64String(raw.Skip(SaltBytes).Take(IvBytes).ToArray()));
            }

            Assert.AreEqual(Samples, salts.Count, "The salt is not random per call.");
            Assert.AreEqual(Samples, ivs.Count, "The IV is not random per call.");
        }

        #endregion

        #region Round-trip and wrong password

        [DataTestMethod]
        [DynamicData(nameof(AllRepresentativeSecrets))]
        public void EncryptThenDecrypt_RoundTrips(string secret, string passPhrase)
        {
            Assert.AreEqual(secret, StringCipher.Decrypt(StringCipher.Encrypt(secret, passPhrase), passPhrase));
        }

        /// <summary>
        /// For this fixed vector and this fixed wrong password the outcome is deterministic, so the
        /// exception can be asserted exactly rather than merely tolerated.
        /// </summary>
        /// <remarks>
        /// The format is unauthenticated CBC, so a wrong key yields valid PKCS7 padding roughly 1 time
        /// in 255 across arbitrary inputs. This test therefore pins one known pair; it is deliberately
        /// not a claim that every wrong password is reliably detected.
        /// </remarks>
        [TestMethod]
        public void Decrypt_WithWrongPassword_ThrowsCryptographicException()
        {
            Assert.ThrowsException<CryptographicException>(
                () => StringCipher.Decrypt(GoldenAsciiCipher, "NotThePassword"),
                "A wrong password must fail loudly. The installer relies on this to report 'could not " +
                "decrypt' rather than loading garbage.");
        }

        #endregion

        #region Config level

        [TestMethod]
        public void LoadFromJson_ConfigContainingShippedCiphertext_DecryptsAllSecrets()
        {
            var onDisk = SolutionInstallConfig.NewConfig();
            onDisk.SQLServerAdminPasswordHash = GoldenAsciiCipher;
            onDisk.InstallerAccount.SecretHash = GoldenSpecialCipher;
            onDisk.ActivityAccount.SecretHash = GoldenGreekCipher;

            var result = SolutionInstallConfig.LoadFromJson(JsonConvert.SerializeObject(onDisk), PassPhrase);

            Assert.IsTrue(result.DecryptedOk,
                "The installer reported it could not decrypt a config file written by the shipping build.");
            Assert.AreEqual(GoldenAsciiPlain, result.Config.SQLServerAdminPassword, "SQL admin password did not survive.");
            Assert.AreEqual(GoldenSpecialPlain, result.Config.InstallerAccount.Secret, "Installer account secret did not survive.");
            Assert.AreEqual(GoldenGreekPlain, result.Config.ActivityAccount.Secret, "Activity account secret did not survive.");
        }

        [TestMethod]
        public void ToJsonThenLoadFromJson_RoundTripsSecrets()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.SQLServerAdminPassword = GoldenAsciiPlain;
            config.InstallerAccount.Secret = GoldenGreekPlain;
            config.ActivityAccount.Secret = GoldenSpecialPlain;

            var result = SolutionInstallConfig.LoadFromJson(config.ToJson(PassPhrase), PassPhrase);

            Assert.IsTrue(result.DecryptedOk);
            Assert.AreEqual(GoldenAsciiPlain, result.Config.SQLServerAdminPassword);
            Assert.AreEqual(GoldenGreekPlain, result.Config.InstallerAccount.Secret);
            Assert.AreEqual(GoldenSpecialPlain, result.Config.ActivityAccount.Secret);
        }

        /// <summary>
        /// Secrets must never reach the file in the clear.
        /// </summary>
        /// <remarks>
        /// Walks the parsed JSON rather than substring-matching the raw text. A raw
        /// <c>Contains</c> check is defeated by JSON escaping: a secret containing a quote or
        /// backslash would appear escaped in the document and the naive check would pass even though
        /// the secret had leaked.
        /// </remarks>
        [TestMethod]
        public void ToJson_DoesNotPersistSecretsInPlainText()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.SQLServerAdminPassword = GoldenSpecialPlain;
            config.InstallerAccount.Secret = GoldenGreekPlain;
            config.ActivityAccount.Secret = GoldenAsciiPlain;

            var json = config.ToJson(PassPhrase);

            StringAssert.Contains(json, "SQLServerAdminPasswordHash", "Expected the encrypted property to be persisted.");

            var decodedValues = JObject.Parse(json)
                .Descendants()
                .OfType<JValue>()
                .Where(v => v.Type == JTokenType.String)
                .Select(v => (string)v.Value)
                .ToList();

            foreach (var secret in new[] { GoldenSpecialPlain, GoldenGreekPlain, GoldenAsciiPlain })
            {
                Assert.IsFalse(decodedValues.Contains(secret),
                    $"A secret was written to the config file in plain text (as a JSON string value).");
            }
        }

        /// <summary>
        /// A config saved with one password must not open with another, and the installer must be told.
        /// </summary>
        [TestMethod]
        public void LoadFromJson_WithWrongPassword_ReportsDecryptionFailed()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.SQLServerAdminPassword = GoldenAsciiPlain;
            config.InstallerAccount.Secret = GoldenGreekPlain;

            var result = SolutionInstallConfig.LoadFromJson(config.ToJson(PassPhrase), "NotThePassword");

            Assert.IsFalse(result.DecryptedOk,
                "The installer must report a failed decryption so it can prompt for the password again, " +
                "rather than silently loading garbage or an empty secret.");
            Assert.AreNotEqual(GoldenAsciiPlain, result.Config.SQLServerAdminPassword,
                "The wrong password recovered the real SQL admin password.");
        }

        #endregion

        #region Encoding

        [TestMethod]
        public void Decrypt_NonLatinSecret_IsUtf8Encoded()
        {
            var decrypted = StringCipher.Decrypt(GoldenGreekCipher, PassPhrase);

            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes(GoldenGreekPlain),
                Encoding.UTF8.GetBytes(decrypted),
                "Non-Latin secret did not survive the UTF8 encoding boundary.");
        }

        #endregion

        /// <summary>
        /// An independent implementation of the ORIGINAL on-disk format, written only against BCL
        /// primitives and deliberately never calling <see cref="StringCipher"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the fixed reference that makes "new -> old" compatibility testable. It must never be
        /// changed to track a change in <see cref="StringCipher"/>; that would defeat its entire purpose.
        /// </para>
        /// <para>
        /// It relies on <c>RijndaelManaged</c> with a 256-bit block, which only works on .NET Framework -
        /// on modern .NET that type is implemented over AES and rejects the larger block. That is the very
        /// defect issue #520 is about, so on any other runtime the oracle reports itself unavailable and
        /// the tests using it return Inconclusive rather than failing misleadingly.
        /// </para>
        /// </remarks>
        static class LegacyFormatOracle
        {
            const int Keysize = 256;
            const int DerivationIterations = 1000;

            static readonly Lazy<bool> _available = new Lazy<bool>(Probe);

            /// <summary>
            /// Runtime capability probe rather than a compile-time check. Legacy (non-SDK) csproj files
            /// do not define NETFRAMEWORK, so a <c>#if</c> here would silently disable these tests - and
            /// a silently skipped compatibility test is worse than none. Asking the runtime whether it can
            /// actually build a 256-bit-block Rijndael is also the exact question that matters.
            /// </summary>
            static bool Probe()
            {
                try
                {
                    using (var rijndael = new RijndaelManaged())
                    {
                        rijndael.BlockSize = 256;
                        rijndael.Mode = CipherMode.CBC;
                        rijndael.Padding = PaddingMode.PKCS7;
                        using (rijndael.CreateDecryptor(new byte[Keysize / 8], new byte[IvBytes]))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception)
                {
                    // Modern .NET implements RijndaelManaged over AES and rejects the 256-bit block.
                    return false;
                }
            }

            public static bool IsAvailable => _available.Value;

            public static string UnavailableReason =>
                "The legacy-format oracle needs RijndaelManaged with a 256-bit block, which only works on " +
                ".NET Framework - modern .NET implements that type over AES and rejects the larger block. " +
                "Run this test on the net48 leg to verify new -> old compatibility.";

            public static string Decrypt(string cipherText, string passPhrase)
            {
                var all = Convert.FromBase64String(cipherText);
                var salt = all.Take(SaltBytes).ToArray();
                var iv = all.Skip(SaltBytes).Take(IvBytes).ToArray();
                var payload = all.Skip(SaltBytes + IvBytes).ToArray();

                using (var kdf = new Rfc2898DeriveBytes(passPhrase, salt, DerivationIterations))
                using (var rijndael = new RijndaelManaged())
                {
                    rijndael.BlockSize = 256;
                    rijndael.Mode = CipherMode.CBC;
                    rijndael.Padding = PaddingMode.PKCS7;

                    using (var decryptor = rijndael.CreateDecryptor(kdf.GetBytes(Keysize / 8), iv))
                    using (var input = new MemoryStream(payload))
                    using (var crypto = new CryptoStream(input, decryptor, CryptoStreamMode.Read))
                    using (var output = new MemoryStream())
                    {
                        // Read to the end rather than trusting a single Read call to return everything.
                        crypto.CopyTo(output);
                        return Encoding.UTF8.GetString(output.ToArray());
                    }
                }
            }
        }
    }
}
