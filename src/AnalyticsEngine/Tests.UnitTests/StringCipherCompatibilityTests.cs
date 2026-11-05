using App.ControlPanel.Engine;
using Common.Entities.Installer;
using DataUtils;
using Microsoft.Extensions.Logging;
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
    /// Locks the on-disk formats of installer configuration secrets (issue #520, and version 2 from the
    /// November 2026 review).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="StringCipher"/> protects the secrets persisted in every customer's installer config
    /// file - the SQL admin password and the app registration client secrets. A config file MUST open
    /// with the same password that saved it, regardless of which build of the installer saved it, and
    /// regardless of which .NET runtime that build targets.
    /// </para>
    /// <para>
    /// The format is versioned. Version 1 (PBKDF2-SHA1 at 1,000 iterations, unauthenticated) is read only;
    /// <see cref="StringCipher.Encrypt"/> writes version 2 (PBKDF2-SHA256 at 600,000 iterations, AES-256-CBC
    /// with an HMAC-SHA256 tag). Both directions are tested for each version that is written:
    /// </para>
    /// <list type="bullet">
    /// <item><b>old -> new</b>: the golden vectors. Stored ciphertext constants - version 1 produced by the
    /// shipping .NET Framework 4.8 build, version 2 produced by an independent BCL implementation of the
    /// documented format - which the current implementation must still decrypt. These are deliberately
    /// captured constants rather than values the test encrypts first, because an encrypt-then-decrypt
    /// round trip passes on any implementation that is <i>consistently</i> wrong.</item>
    /// <item><b>new -> reader</b>: <see cref="CurrentFormatOracle"/>. An independent implementation of
    /// version 2 using only BCL primitives, which must be able to read whatever <see cref="StringCipher.Encrypt"/>
    /// writes today. Without it, a writer that drifted from the documented format would leave every golden
    /// vector green while producing files that a later build, written to the documentation, could not open.</item>
    /// </list>
    /// <para>
    /// <see cref="LegacyFormatOracle"/> is an independent reader of version 1. It also proves the one
    /// deliberate incompatibility: an installer built before version 2 cannot open a value written today,
    /// and refuses it rather than decrypting it to noise.
    /// </para>
    /// <para>
    /// <b>Do not regenerate the golden vectors.</b> If a change makes them fail, the change has
    /// broken backwards compatibility with config files in the field - fix the code, not the vectors.
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

        // -----------------------------------------------------------------------------------------
        // Version 2 golden vectors - produced by an independent BCL implementation of the documented
        // format (Rfc2898DeriveBytes SHA-256, HMACSHA256, Aes), never by StringCipher itself, which
        // uses BouncyCastle. The first four share one salt, as values saved together in one process do.
        // -----------------------------------------------------------------------------------------

        const string GoldenV2AsciiCipher =
            "v2:6mT2JRxVskHnWeshEL6NMjBpNlnioX/Vsshiq4LH9yu0uq0BYJlaDLttJrtvdTXAbMLn5ysQ51M86K0jOLerC+Fd+yxdiY0gl/IItFGJYcCbhK51clkgVy/5COAZxD6T";
        const string GoldenV2GreekCipher =
            "v2:6mT2JRxVskHnWeshEL6NMqT0lr5/5qrgFZWXTlXat0K2qlO8kdKFFQryN+RB7YSnK5GM832jwxrOKz2BL8B+Ra0NDTXnsQrLJnmilVVgtH4Z7KqTovNwVokI8ggrlOSF";

        // Exactly one AES block (16 bytes): PKCS7 appends a whole extra block.
        const string GoldenV2Block16Plain = "0123456789abcdef";
        const string GoldenV2Block16Cipher =
            "v2:6mT2JRxVskHnWeshEL6NMutcl92sNqZWy8sPR9JLKXkU/HtG7EkhnWo5bUiSsV+RcXee+W+/SK1NqnxdIqHkX+8KPiFvUtcL1GjpEHYKRachAqXruYAQ/22paa2R+QVE";
        const string GoldenV2EmptyCipher =
            "v2:6mT2JRxVskHnWeshEL6NMp/OokiEvFNVomgqdC+1ztOBl1nhwnmExq8DbiW2uAGbpVvS5tagCAvsmWE+ZJWl7ZAAk0ZLAVPJMKkCWF8aRwM=";

        // A non-ASCII passphrase: the KDF takes its UTF8 bytes.
        const string GoldenV2UnicodePassCipher =
            "v2:lRzSnOFDb65iI/a2i2jU4s6yn2Jeq6BHk0UAn16p3Ll1dXRy1IMYsoxhb0B1ybLhNGSEjCLioYohYBupwpfW8qcSP1EnHg4f35Ff5jmbnWg=";

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
            // producing files no other build can open.
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

        [DataTestMethod]
        [DataRow(GoldenV2AsciiCipher, PassPhrase, GoldenAsciiPlain, DisplayName = "v2 ASCII secret")]
        [DataRow(GoldenV2GreekCipher, PassPhrase, GoldenGreekPlain, DisplayName = "v2 non-Latin secret")]
        [DataRow(GoldenV2Block16Cipher, PassPhrase, GoldenV2Block16Plain, DisplayName = "v2 exactly one block")]
        [DataRow(GoldenV2EmptyCipher, PassPhrase, GoldenEmptyPlain, DisplayName = "v2 empty secret")]
        [DataRow(GoldenV2UnicodePassCipher, GoldenUnicodePass, GoldenUnicodePassPlain, DisplayName = "v2 non-Latin passphrase")]
        public void Decrypt_CurrentFormatGoldenVectors_StillOpen(string cipherText, string passPhrase, string expectedPlainText)
        {
            Assert.AreEqual(expectedPlainText, StringCipher.Decrypt(cipherText, passPhrase),
                "A version 2 value no longer decrypts. Config files saved since version 2 would stop opening.");
        }

        /// <summary>
        /// The upgrade path: a file saved by an older build opens, and saving it writes every secret in the
        /// current format, so its secrets stop being protected by the cheap version 1 key derivation.
        /// </summary>
        [TestMethod]
        public void SavingAConfigOpenedFromVersion1_ReEncryptsEverySecretInTheCurrentFormat()
        {
            var onDisk = SolutionInstallConfig.NewConfig();
            onDisk.SQLServerAdminPasswordHash = GoldenAsciiCipher;
            onDisk.InstallerAccount.SecretHash = GoldenSpecialCipher;
            onDisk.ActivityAccount.SecretHash = GoldenGreekCipher;
            var opened = SolutionInstallConfig.LoadFromJson(JsonConvert.SerializeObject(onDisk), PassPhrase);
            Assert.IsTrue(opened.DecryptedOk);

            var saved = JObject.Parse(opened.Config.ToJson(PassPhrase));

            var hashes = new[]
            {
                (string)saved["SQLServerAdminPasswordHash"],
                (string)saved.SelectToken("InstallerAccount.SecretHash"),
                (string)saved.SelectToken("ActivityAccount.SecretHash"),
            };
            foreach (var hash in hashes)
            {
                Assert.IsTrue(StringCipher.IsCurrentFormat(hash), "A secret was saved in the old format: " + hash.Substring(0, 8));
            }
            var reopened = SolutionInstallConfig.LoadFromJson(saved.ToString(), PassPhrase);
            Assert.IsTrue(reopened.DecryptedOk);
            Assert.AreEqual(GoldenAsciiPlain, reopened.Config.SQLServerAdminPassword);
            Assert.AreEqual(GoldenSpecialPlain, reopened.Config.InstallerAccount.Secret);
            Assert.AreEqual(GoldenGreekPlain, reopened.Config.ActivityAccount.Secret);
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

        #region new -> reader : what this build writes is the documented current format

        /// <summary>
        /// The direction golden vectors alone cannot cover.
        /// </summary>
        /// <remarks>
        /// Decrypts fresh <see cref="StringCipher.Encrypt"/> output using <see cref="CurrentFormatOracle"/>, an
        /// independent implementation of version 2 over BCL primitives. Without this, a writer that drifted from the
        /// documented format would still read its own output and every stored vector, while producing files a build
        /// written to the documentation could not open.
        /// </remarks>
        [DataTestMethod]
        [DynamicData(nameof(AllRepresentativeSecrets))]
        public void Encrypt_OutputIsReadableByAnIndependentCurrentFormatDecoder(string secret, string passPhrase)
        {
            var cipherText = StringCipher.Encrypt(secret, passPhrase);

            Assert.IsTrue(StringCipher.IsCurrentFormat(cipherText), "Encrypt must write the current format.");
            Assert.AreEqual(secret, CurrentFormatOracle.Decrypt(cipherText, passPhrase),
                "Ciphertext written by this build could not be read by an independent implementation of the documented format.");
        }

        /// <summary>The oracle must itself be correct, or the test above proves nothing.</summary>
        [TestMethod]
        public void CurrentFormatOracle_ReadsTheStoredGoldenVectors()
        {
            Assert.AreEqual(GoldenAsciiPlain, CurrentFormatOracle.Decrypt(GoldenV2AsciiCipher, PassPhrase));
            Assert.AreEqual(GoldenGreekPlain, CurrentFormatOracle.Decrypt(GoldenV2GreekCipher, PassPhrase));
            Assert.AreEqual(GoldenUnicodePassPlain, CurrentFormatOracle.Decrypt(GoldenV2UnicodePassCipher, GoldenUnicodePass));
        }

        /// <summary>
        /// The one deliberate incompatibility: an installer built before version 2 can't open a value written today.
        /// It must refuse it - which it reports as a wrong password - rather than decrypt it to noise it would keep.
        /// </summary>
        [TestMethod]
        public void AnInstallerBuiltBeforeVersion2_RefusesTheCurrentFormat()
        {
            if (!LegacyFormatOracle.IsAvailable)
                Assert.Inconclusive(LegacyFormatOracle.UnavailableReason);

            var cipherText = StringCipher.Encrypt(GoldenAsciiPlain, PassPhrase);

            Assert.ThrowsException<FormatException>(() => LegacyFormatOracle.Decrypt(cipherText, PassPhrase),
                "The version 2 marker must make every version 1 reader fail at once.");
        }

        /// <summary>
        /// The legacy oracle must itself be correct, or the golden vectors prove nothing about the version 1 reader.
        /// Verify it against the stored vectors, which were produced by the real shipping build.
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

        #region Current format: authenticated, and costly to guess

        /// <summary>Any change to any part of a version 2 value is refused, never decrypted.</summary>
        [TestMethod]
        public void Decrypt_CurrentFormat_RefusesAnyAlteredByte()
        {
            var raw = Convert.FromBase64String(GoldenV2AsciiCipher.Substring(StringCipher.CurrentFormatPrefix.Length));

            // The salt, the IV, a ciphertext block and the tag.
            foreach (var index in new[] { 0, 16, 32, raw.Length - 1 })
            {
                var altered = (byte[])raw.Clone();
                altered[index] ^= 0x01;
                Assert.ThrowsException<CryptographicException>(
                    () => StringCipher.Decrypt(StringCipher.CurrentFormatPrefix + Convert.ToBase64String(altered), PassPhrase),
                    $"A value altered at byte {index} was accepted.");
            }
            Assert.AreEqual(GoldenAsciiPlain, StringCipher.Decrypt(GoldenV2AsciiCipher, PassPhrase), "The unaltered value must still open.");
        }

        /// <summary>Unlike version 1, whose padding check lets about one wrong password in 256 through, every wrong password fails.</summary>
        [TestMethod]
        public void Decrypt_CurrentFormat_WithWrongPassword_AlwaysThrows()
        {
            Assert.ThrowsException<CryptographicException>(() => StringCipher.Decrypt(GoldenV2AsciiCipher, "NotThePassword"));
        }

        [TestMethod]
        public void Decrypt_AFormatFromANewerInstaller_IsRefusedAsUnknown()
        {
            var future = "v3:" + GoldenV2AsciiCipher.Substring(StringCipher.CurrentFormatPrefix.Length);

            var refused = Assert.ThrowsException<CryptographicException>(() => StringCipher.Decrypt(future, PassPhrase));
            StringAssert.Contains(refused.Message, "format this installer doesn't know");
        }

        [TestMethod]
        public void CurrentFormat_UsesPbkdf2Sha256AtLeast600kIterations()
        {
            Assert.IsTrue(StringCipher.CurrentIterations >= 600000,
                "OWASP's 2023 floor for PBKDF2-HMAC-SHA256 is 600,000 iterations.");
            Assert.AreEqual(CurrentFormatOracle.Iterations, StringCipher.CurrentIterations,
                "The documented iteration count and the implementation disagree; the golden vectors would catch this too.");
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
        /// The IV must be random on every call. The salt is random per process and passphrase (see
        /// <see cref="StringCipher"/>), so the 600,000-iteration derivation runs once per save rather than once
        /// per secret - but a new process must not reuse it. Comparing whole ciphertexts would pass if only
        /// one of the two varied, so the fields are compared independently.
        /// </summary>
        [TestMethod]
        public void Encrypt_RandomisesTheIvOnEveryCall_AndTheSaltForEachProcess()
        {
            const int Samples = 8;
            const int V2SaltBytes = 16;
            const int V2IvBytes = 16;
            var salts = new HashSet<string>();
            var ivs = new HashSet<string>();

            for (var i = 0; i < Samples; i++)
            {
                var raw = Convert.FromBase64String(StringCipher.Encrypt(GoldenAsciiPlain, PassPhrase).Substring(StringCipher.CurrentFormatPrefix.Length));
                salts.Add(Convert.ToBase64String(raw.Take(V2SaltBytes).ToArray()));
                ivs.Add(Convert.ToBase64String(raw.Skip(V2SaltBytes).Take(V2IvBytes).ToArray()));
            }

            Assert.AreEqual(Samples, ivs.Count, "The IV is not random per call.");
            Assert.AreEqual(1, salts.Count, "One process should derive one key per passphrase.");

            StringCipher.ResetKeyCache();
            var afterRestart = Convert.FromBase64String(StringCipher.Encrypt(GoldenAsciiPlain, PassPhrase).Substring(StringCipher.CurrentFormatPrefix.Length));
            Assert.IsFalse(salts.Contains(Convert.ToBase64String(afterRestart.Take(V2SaltBytes).ToArray())),
                "A new process must pick a new salt.");
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
        /// Version 1 is unauthenticated CBC, so a wrong key yields valid PKCS7 padding roughly 1 time
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

        #region The copy older installers can open, kept by the first save over a version 1 file

        /// <summary>
        /// A configuration file as an installer from before version 2 left it, with every secret in version 1. Indented and with
        /// a byte order mark, unlike anything this build writes, so only a byte-for-byte copy can match it.
        /// </summary>
        static byte[] Version1ConfigFile(string resourceGroupName = "contoso-analytics")
        {
            var config = SolutionInstallConfig.NewConfig();
            config.ResourceGroupName = resourceGroupName;
            config.Tags.Add(new AzTag("Purpose", "Καλημέρα κόσμε"));
            config.SQLServerAdminPasswordHash = GoldenAsciiCipher;
            config.InstallerAccount.SecretHash = GoldenSpecialCipher;
            config.ActivityAccount.SecretHash = GoldenGreekCipher;
            config.ConfigSchemaVersion = new Version(3, 5, 0);
            var text = JsonConvert.SerializeObject(config, Formatting.Indented) + "\r\n";
            return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        }

        static string[] SavedSecrets(string path)
        {
            var saved = JObject.Parse(File.ReadAllText(path));
            return new[]
            {
                (string)saved["SQLServerAdminPasswordHash"],
                (string)saved.SelectToken("InstallerAccount.SecretHash"),
                (string)saved.SelectToken("ActivityAccount.SecretHash"),
            };
        }

        static bool FailOnSaveError(string error)
        {
            Assert.Fail("The save failed: " + error);
            return false;
        }

        static void InScratchDirectory(Action<string> test)
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "contoso-v1-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                test(directory);
            }
            finally
            {
                foreach (var file in Directory.GetFiles(directory)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void TheFirstSaveOverAVersion1File_KeepsAByteForByteCopyFirst()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var original = Version1ConfigFile();
                File.WriteAllBytes(path, original);
                var opened = SolutionInstallConfig.LoadFromFile(path, PassPhrase);
                Assert.IsTrue(opened.CanBecomeSaveTarget, "Fixture: the file should open completely.");
                var notices = new List<string>();

                Assert.IsTrue(opened.Config.Save(path, PassPhrase, FailOnSaveError, notices.Add));

                var backup = Path.Combine(directory, "Contoso.v1-backup.json");
                CollectionAssert.AreEqual(original, File.ReadAllBytes(backup), "The copy must be the file exactly as it was on disk.");
                CollectionAssert.AreEqual(new[] { GoldenAsciiCipher, GoldenSpecialCipher, GoldenGreekCipher }, SavedSecrets(backup),
                    "The copy keeps the version 1 values, which installers from before version 2 decrypt.");
                Assert.IsTrue(SavedSecrets(path).All(StringCipher.IsCurrentFormat), "The file itself is saved in the current format.");

                var reopened = SolutionInstallConfig.LoadFromFile(path, PassPhrase);
                Assert.IsTrue(reopened.DecryptedOk);
                Assert.AreEqual(GoldenAsciiPlain, reopened.Config.SQLServerAdminPassword);
                Assert.AreEqual(GoldenSpecialPlain, reopened.Config.InstallerAccount.Secret);
                Assert.AreEqual(GoldenGreekPlain, reopened.Config.ActivityAccount.Secret);
                Assert.AreEqual("Καλημέρα κόσμε", reopened.Config.Tags.Single().Value);

                Assert.AreEqual(1, notices.Count, "The operator must be told about the copy.");
                StringAssert.Contains(notices[0], backup);
                StringAssert.Contains(notices[0], "older installers can open");
                StringAssert.Contains(notices[0], "older, weaker encryption");
                StringAssert.Contains(notices[0], "delete it");
            });
        }

        [TestMethod]
        public void SavingAgain_LeavesTheCopyAlone()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var original = Version1ConfigFile();
                File.WriteAllBytes(path, original);
                var config = SolutionInstallConfig.LoadFromFile(path, PassPhrase).Config;
                var notices = new List<string>();
                Assert.IsTrue(config.Save(path, PassPhrase, FailOnSaveError, notices.Add));
                var backup = Version1ConfigBackup.PathFor(path);
                var copiedAt = File.GetLastWriteTimeUtc(backup);

                config.ResourceGroupName = "contoso-analytics-renamed";
                Assert.IsTrue(config.Save(path, PassPhrase, FailOnSaveError, notices.Add));

                CollectionAssert.AreEqual(original, File.ReadAllBytes(backup));
                Assert.AreEqual(copiedAt, File.GetLastWriteTimeUtc(backup), "The copy was written again.");
                Assert.AreEqual(1, notices.Count, "Only the save that changed the format has anything to tell.");
                Assert.AreEqual("contoso-analytics-renamed", (string)JObject.Parse(File.ReadAllText(path))["ResourceGroupName"]);
                CollectionAssert.AreEquivalent(new[] { path, backup }, Directory.GetFiles(directory), "One copy, and no other file.");
            });
        }

        [TestMethod]
        public void AnExistingCopy_IsNeverReplaced()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var backup = Path.Combine(directory, "Contoso.v1-backup.json");
                // Kept the first time round, before the operator went back to an older installer and saved from it.
                var firstCopy = Version1ConfigFile("contoso-analytics-first");
                File.WriteAllBytes(backup, firstCopy);
                File.WriteAllBytes(path, Version1ConfigFile());
                var config = SolutionInstallConfig.LoadFromFile(path, PassPhrase).Config;
                var notices = new List<string>();

                Assert.IsTrue(config.Save(path, PassPhrase, FailOnSaveError, notices.Add));

                CollectionAssert.AreEqual(firstCopy, File.ReadAllBytes(backup), "An existing copy may be the only one an older installer can open.");
                Assert.IsTrue(SavedSecrets(path).All(StringCipher.IsCurrentFormat));
                Assert.AreEqual(1, notices.Count);
                StringAssert.Contains(notices[0], backup);
                StringAssert.Contains(notices[0], "already existed");
            });
        }

        [DataTestMethod]
        [DataRow("new", DisplayName = "A new file")]
        [DataRow("version 2", DisplayName = "A file this installer saved")]
        [DataRow("no secret", DisplayName = "An older file with no secret saved")]
        public void AFileWithNoVersion1Secret_GetsNoCopy(string onDisk)
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var config = SolutionInstallConfig.NewConfig();
                config.SQLServerAdminPassword = GoldenAsciiPlain;
                config.InstallerAccount.Secret = GoldenSpecialPlain;
                if (onDisk == "version 2") Assert.IsTrue(config.Save(path, PassPhrase, FailOnSaveError, notice => Assert.Fail(notice)));
                if (onDisk == "no secret") File.WriteAllText(path, JsonConvert.SerializeObject(SolutionInstallConfig.NewConfig()));
                var notices = new List<string>();

                Assert.IsTrue(config.Save(path, PassPhrase, FailOnSaveError, notices.Add));

                CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(directory), "An older installer loses nothing, so there is nothing to copy.");
                Assert.AreEqual(0, notices.Count);
                Assert.IsTrue(SavedSecrets(path).Where(secret => secret.Length > 0).All(StringCipher.IsCurrentFormat));
            });
        }

        [TestMethod]
        public void TheNetworkingCheckpoint_KeepsTheCopyToo()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var original = Version1ConfigFile();
                File.WriteAllBytes(path, original);
                // An installation run from a file that was opened but not saved again: its first checkpoint is the first save.
                var config = SolutionInstallConfig.LoadFromFile(path, PassPhrase).Config;
                var log = new CapturingLogger();

                config.CheckpointPrivateNetworking(PassPhrase, log);
                config.CheckpointPrivateNetworking(PassPhrase, log);

                var backup = Version1ConfigBackup.PathFor(path);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(backup));
                Assert.IsTrue(SavedSecrets(path).All(StringCipher.IsCurrentFormat));
                Assert.IsNotNull(JObject.Parse(File.ReadAllText(path))["PrivateNetworking"], "The checkpoint still saves what it is for.");
                StringAssert.Contains(log.Single(LogLevel.Warning), backup, "Logged once, by the checkpoint that kept the copy.");
                Assert.IsFalse(File.Exists(path + ".network-checkpoint-new"));
            });
        }

        [TestMethod]
        public void WhenTheCopyCannotBeWritten_SaveLeavesTheFileUnchanged_UntilItCanBe()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var original = Version1ConfigFile();
                File.WriteAllBytes(path, original);
                var config = SolutionInstallConfig.LoadFromFile(path, PassPhrase).Config;
                var backup = Version1ConfigBackup.PathFor(path);
                // A folder by that name: creating the copy fails, as it does in a folder the operator can't add files to.
                Directory.CreateDirectory(backup);
                var errors = new List<string>();
                var notices = new List<string>();

                Assert.IsFalse(config.Save(path, PassPhrase, error => { errors.Add(error); return false; }, notices.Add));

                CollectionAssert.AreEqual(original, File.ReadAllBytes(path), "The file older installers can open was overwritten with no copy.");
                Assert.AreEqual(1, errors.Count);
                StringAssert.Contains(errors[0], "was not saved, and is unchanged");
                StringAssert.Contains(errors[0], backup);
                StringAssert.Contains(errors[0], "Save Configuration As");
                Assert.AreEqual(0, notices.Count);

                // Retried once the copy can be written, the save goes ahead, copy first.
                Assert.IsTrue(config.Save(path, PassPhrase, error => { Directory.Delete(backup); return true; }, notices.Add));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(backup));
                Assert.IsTrue(SavedSecrets(path).All(StringCipher.IsCurrentFormat));
                Assert.AreEqual(1, notices.Count);
            });
        }

        /// <summary>
        /// The copy is written, then the file itself can't be: the copy stays, and the save that does write the file says where
        /// it is as a copy of the file as it was, not as one that "already existed".
        /// </summary>
        [TestMethod]
        public void WhenOnlyTheFileCannotBeWritten_TheCopyStays_AndTheSaveThatWritesItReportsIt()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var original = Version1ConfigFile();
                File.WriteAllBytes(path, original);
                var config = SolutionInstallConfig.LoadFromFile(path, PassPhrase).Config;
                File.SetAttributes(path, FileAttributes.ReadOnly);
                var errors = new List<string>();
                var notices = new List<string>();

                Assert.IsFalse(config.Save(path, PassPhrase, error => { errors.Add(error); return false; }, notices.Add));

                Assert.AreEqual(1, errors.Count, "A file that can't be written is reported, and the save can be retried.");
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(Version1ConfigBackup.PathFor(path)));
                Assert.AreEqual(0, notices.Count, "Nothing was saved.");

                File.SetAttributes(path, FileAttributes.Normal);
                Assert.IsTrue(config.Save(path, PassPhrase, FailOnSaveError, notices.Add));
                Assert.AreEqual(1, notices.Count);
                StringAssert.Contains(notices[0], "kept a copy of the file as it was");
                Assert.IsTrue(SavedSecrets(path).All(StringCipher.IsCurrentFormat));
            });
        }

        [TestMethod]
        public void WhenTheCopyCannotBeWritten_TheNetworkingCheckpointStopsTheInstallation()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var original = Version1ConfigFile();
                File.WriteAllBytes(path, original);
                var config = SolutionInstallConfig.LoadFromFile(path, PassPhrase).Config;
                var backup = Version1ConfigBackup.PathFor(path);
                Directory.CreateDirectory(backup);

                var refused = Assert.ThrowsException<CloudInstallEngine.Models.InstallException>(
                    () => config.CheckpointPrivateNetworking(PassPhrase, new CapturingLogger()));

                StringAssert.Contains(refused.Message, "Private networking was not changed");
                StringAssert.Contains(refused.Message, backup);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path), "The file older installers can open was overwritten with no copy.");
                Assert.IsFalse(File.Exists(path + ".network-checkpoint-new"));
            });
        }

        /// <summary>Installing saves first, and must not start when that save could not keep the copy.</summary>
        [TestMethod]
        public void Installing_DoesNotStart_WhileTheSaveBeforeItCannotKeepTheCopy()
        {
            InScratchDirectory(directory =>
            {
                var path = Path.Combine(directory, "Contoso.json");
                var original = Version1ConfigFile();
                File.WriteAllBytes(path, original);
                var backup = Version1ConfigBackup.PathFor(path);
                Directory.CreateDirectory(backup);
                var notices = new List<string>();

                InstallTests.InstallerWorkspaceTests.RunSta(() =>
                {
                    using (var form = new App.ControlPanel.MainForm())
                    {
                        form.SelectedUI = form;
                        form.RetryFailedSave = error => false;
                        form.ShowSaveNotice = notices.Add;
                        form.OpenConfigFile(path, () => PassPhrase, _ => System.Windows.Forms.DialogResult.Cancel, error => Assert.Fail(error));

                        // What the Install button calls before it starts anything.
                        Assert.IsFalse(form.SaveLastSettings(), "The installation would start with the configuration unsaved.");
                        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                        Assert.AreEqual(path, form.LastConfigFilePath, "Save still writes where it did.");

                        Directory.Delete(backup);
                        Assert.IsTrue(form.SaveLastSettings());
                    }
                });

                CollectionAssert.AreEqual(original, File.ReadAllBytes(backup));
                Assert.IsTrue(SavedSecrets(path).All(StringCipher.IsCurrentFormat));
                Assert.AreEqual(1, notices.Count);
            });
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
        /// An independent implementation of the CURRENT (version 2) format, written only against BCL primitives
        /// and deliberately never calling <see cref="StringCipher"/>, which uses BouncyCastle. Like
        /// <see cref="LegacyFormatOracle"/>, it must never be changed to track a change in <see cref="StringCipher"/>.
        /// </summary>
        static class CurrentFormatOracle
        {
            public const int Iterations = 600000;
            static readonly Dictionary<string, byte[]> MasterKeys = new Dictionary<string, byte[]>();

            public static string Decrypt(string cipherText, string passPhrase)
            {
                Assert.IsTrue(cipherText.StartsWith("v2:", StringComparison.Ordinal), "Not a version 2 value.");
                var all = Convert.FromBase64String(cipherText.Substring(3));
                var salt = all.Take(16).ToArray();
                var iv = all.Skip(16).Take(16).ToArray();
                var cipher = all.Skip(32).Take(all.Length - 32 - 32).ToArray();
                var tag = all.Skip(all.Length - 32).ToArray();

                var master = MasterKey(passPhrase, salt);
                byte[] encryptionKey, authenticationKey, expected;
                using (var hmac = new HMACSHA256(master))
                {
                    encryptionKey = hmac.ComputeHash(Encoding.ASCII.GetBytes("StringCipher.v2.encryption"));
                    authenticationKey = hmac.ComputeHash(Encoding.ASCII.GetBytes("StringCipher.v2.authentication"));
                }
                using (var hmac = new HMACSHA256(authenticationKey))
                    expected = hmac.ComputeHash(Encoding.ASCII.GetBytes("v2").Concat(salt).Concat(iv).Concat(cipher).ToArray());
                CollectionAssert.AreEqual(expected, tag, "The authentication tag does not match the documented construction.");

                using (var aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var decryptor = aes.CreateDecryptor(encryptionKey, iv))
                        return Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
                }
            }

            static byte[] MasterKey(string passPhrase, byte[] salt)
            {
                var key = passPhrase + "|" + Convert.ToBase64String(salt);
                lock (MasterKeys)
                {
                    if (MasterKeys.TryGetValue(key, out var cached)) return cached;
                }
                using (var kdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(passPhrase), salt, Iterations, HashAlgorithmName.SHA256))
                {
                    var master = kdf.GetBytes(32);
                    lock (MasterKeys) MasterKeys[key] = master;
                    return master;
                }
            }
        }

        /// <summary>
        /// An independent implementation of the ORIGINAL on-disk format, written only against BCL
        /// primitives and deliberately never calling <see cref="StringCipher"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the fixed reference for version 1: it proves the golden vectors were read correctly, and
        /// that an installer built before version 2 refuses what this build writes. It must never be
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
                "Run this test on the net48 leg to verify version 1 compatibility.";

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
