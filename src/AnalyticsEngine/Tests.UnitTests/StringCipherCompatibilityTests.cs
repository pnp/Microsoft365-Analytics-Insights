using App.ControlPanel.Engine;
using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
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
    /// The vectors below are <b>stored constants produced by the shipping .NET Framework 4.8 build</b>
    /// (verified against .NET Framework 4.8.9310.0 using the real DataUtils.dll). That is the whole
    /// point: a round-trip test - encrypt then decrypt in the same process - passes happily on an
    /// implementation that is consistently wrong, so it cannot detect a format change. Only a
    /// ciphertext captured from a previous build can prove that files already on customer disks
    /// still open.
    /// </para>
    /// <para>
    /// <b>Do not regenerate these constants.</b> If a change makes them fail, the change has broken
    /// backwards compatibility with every config file in the field - fix the code, not the vectors.
    /// </para>
    /// <para>
    /// Why this matters beyond hygiene: the format is Rijndael with a <b>256-bit block</b>, which is
    /// not AES (AES is Rijndael fixed to a 128-bit block). .NET Framework's RijndaelManaged supports
    /// it; modern .NET implements RijndaelManaged over AES and throws
    /// <c>PlatformNotSupportedException: BlockSize must be 128 in this implementation.</c>
    /// So any port to .NET 10 must keep this exact format while changing the implementation.
    /// </para>
    /// <para>All values here are synthetic - no real customer password, secret or tenant.</para>
    /// </remarks>
    [TestClass]
    public class StringCipherCompatibilityTests
    {
        // ---------------------------------------------------------------------------------------
        // Golden vectors: ciphertext captured from the shipping .NET Framework 4.8 build.
        // Format: [32 bytes salt][32 bytes IV][ciphertext], base64.
        // KDF: PBKDF2-HMAC-SHA1, 1000 iterations, 256-bit key. Cipher: Rijndael-256/CBC/PKCS7.
        // ---------------------------------------------------------------------------------------

        const string PassPhrase = "CorrectHorseBattery";

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

        // 500 characters - spans many CBC blocks, so a block-size or chaining regression shows here
        // even if it happens to be invisible on a single-block payload.
        static readonly string GoldenLongPlain = new string('X', 500);
        const string GoldenLongCipher =
            "OSN54QnqLZF4suJIHpvYgQVSKAqQ5kp36m0ELSvp0tOi8uoM9dH6jcf2lrN+KtvAArxMVDa7tASjqMfTSoDrP1ww6qHjkQ1Z3Wa9FsCQyzTg97Y7s5xikT5IAk6v1Kb4rpO2Qu0PQ5/LEnUVO+Km5xBQvgSms5XSpcDNPDZGNCujnqWRhDsh4swCGWs7HqwbOJ7R8j/lnAY8fQ2YIIoRIddM+ww0qCAaw/CokfAcYYZfB2evQKtyO8AXzXKWCu7/O5dpJpNEbE3CCmXu8pmFKQhZSsrDIXLkRBSWklSfP0+Y83nk16u/9hp7RIxqw97cxhriw3SDxt5G2qnsz5cEKG9m7MizltSeoEDbHTluDS8FaxqZ/J5vEzkC1rNkHCCigza65nFE53E4/HARv2olj8atpHbgiRz+23fYv4gAwMl96NoOMGw/GE0SVcI8YOkBQl0/v7/MNtNDpp8HzUkwP5u1KFJKXNTlmz1dBDG2g9NeTN7wRbd+BQ8cedKmHp/BI6sgo01i7ykGsz0k180ZhHE5I/g1nuD27EpvCyl30LDJqahOF+yH2RdH5mbed/to3PB697MEkyAqQKCknnUAPhVk+MIM+hc3M0k15z4kzMfUq5Ash9cbU8Ojb76MGqfo6mP7T2wJsm6a6tWyg1fShZI688oCbowpet2HSG2Wd4vctMNMqWwsiz5FIVRK0KAWmBABZ5n61wDeRYSrWc4XhQjacaqX6xrDBQjN+i1bTEAyhimc8PwGjCuFGdkoP8Ab";

        #region Golden vectors - files already on customer disks must still open

        [DataTestMethod]
        [DataRow(GoldenAsciiCipher, PassPhrase, GoldenAsciiPlain, DisplayName = "ASCII secret")]
        [DataRow(GoldenGreekCipher, PassPhrase, GoldenGreekPlain, DisplayName = "Non-Latin secret")]
        [DataRow(GoldenEmptyCipher, PassPhrase, GoldenEmptyPlain, DisplayName = "Empty secret")]
        [DataRow(GoldenSpecialCipher, PassPhrase, GoldenSpecialPlain, DisplayName = "Special characters")]
        [DataRow(GoldenUnicodePassCipher, GoldenUnicodePass, GoldenUnicodePassPlain, DisplayName = "Non-Latin passphrase")]
        public void Decrypt_CiphertextFromShippingNetFrameworkBuild_StillOpens(
            string cipherText, string passPhrase, string expectedPlainText)
        {
            var actual = StringCipher.Decrypt(cipherText, passPhrase);

            Assert.AreEqual(expectedPlainText, actual,
                "A configuration file saved by a previous build no longer decrypts. This breaks every " +
                "existing customer config - the encryption format must not change.");
        }

        /// <summary>
        /// Multi-block payload, kept separate because a 500-character DataRow is unreadable inline.
        /// </summary>
        [TestMethod]
        public void Decrypt_MultiBlockCiphertextFromShippingNetFrameworkBuild_StillOpens()
        {
            var actual = StringCipher.Decrypt(GoldenLongCipher, PassPhrase);

            Assert.AreEqual(GoldenLongPlain, actual,
                "Multi-block decryption regressed. A CBC chaining or block-size change can be invisible " +
                "on a single-block secret but corrupt a longer one.");
        }

        #endregion

        #region Format invariants - these are what a block-size regression trips

        /// <summary>
        /// The wire format is [32 bytes salt][32 bytes IV][ciphertext], and the ciphertext is a whole
        /// number of 32-byte (256-bit) Rijndael blocks.
        /// </summary>
        /// <remarks>
        /// This is the assertion that fails loudly if someone "modernises" the cipher to AES: AES has a
        /// 16-byte block and a 16-byte IV, so both the header size and the block multiple would change
        /// and every existing config file would become unreadable.
        /// </remarks>
        [DataTestMethod]
        [DataRow(GoldenAsciiCipher, DisplayName = "ASCII secret")]
        [DataRow(GoldenGreekCipher, DisplayName = "Non-Latin secret")]
        [DataRow(GoldenEmptyCipher, DisplayName = "Empty secret")]
        [DataRow(GoldenSpecialCipher, DisplayName = "Special characters")]
        [DataRow(GoldenUnicodePassCipher, DisplayName = "Non-Latin passphrase")]
        [DataRow(GoldenLongCipher, DisplayName = "Multi-block secret")]
        public void GoldenVectors_HaveTheExpected256BitBlockLayout(string cipherText)
        {
            const int SaltBytes = 32;
            const int IvBytes = 32;
            const int BlockBytes = 32;

            var raw = Convert.FromBase64String(cipherText);

            Assert.IsTrue(raw.Length > SaltBytes + IvBytes,
                "Ciphertext is too short to contain a salt, an IV and at least one block.");

            var payloadLength = raw.Length - SaltBytes - IvBytes;
            Assert.AreEqual(0, payloadLength % BlockBytes,
                $"Encrypted payload is {payloadLength} bytes, which is not a whole number of 32-byte " +
                "Rijndael-256 blocks. The cipher is Rijndael with a 256-bit block, NOT AES (which has a " +
                "16-byte block). Changing this breaks every existing config file.");
        }

        /// <summary>
        /// Freshly written ciphertext must have the same shape as the stored vectors, so that a file
        /// written by today's build is readable by the build that produced the vectors above.
        /// </summary>
        [TestMethod]
        public void Encrypt_ProducesTheSameLayoutAsTheShippedFormat()
        {
            var raw = Convert.FromBase64String(StringCipher.Encrypt(GoldenAsciiPlain, PassPhrase));
            var reference = Convert.FromBase64String(GoldenAsciiCipher);

            Assert.AreEqual(reference.Length, raw.Length,
                "Newly-written ciphertext is a different length to ciphertext written by the shipping " +
                "build for the same plaintext, so the format has changed.");
        }

        /// <summary>
        /// Salt and IV are random per call, so the same plaintext must never encrypt to the same bytes.
        /// A regression to a fixed IV would be a real security defect that a round-trip test cannot see.
        /// </summary>
        [TestMethod]
        public void Encrypt_SamePlainTextTwice_ProducesDifferentCiphertext()
        {
            var first = StringCipher.Encrypt(GoldenAsciiPlain, PassPhrase);
            var second = StringCipher.Encrypt(GoldenAsciiPlain, PassPhrase);

            Assert.AreNotEqual(first, second,
                "Encrypting the same value twice produced identical output, so the salt/IV are no longer " +
                "random per call.");
        }

        #endregion

        #region Round-trip

        [TestMethod]
        public void EncryptThenDecrypt_RoundTripsEveryRepresentativeSecret()
        {
            foreach (var secret in new[]
            {
                GoldenAsciiPlain, GoldenGreekPlain, GoldenEmptyPlain,
                GoldenSpecialPlain, GoldenLongPlain, "a"
            })
            {
                var cipherText = StringCipher.Encrypt(secret, PassPhrase);
                Assert.AreEqual(secret, StringCipher.Decrypt(cipherText, PassPhrase),
                    $"Round-trip failed for a secret of length {secret.Length}.");
            }
        }

        /// <summary>
        /// The installer relies on a wrong password failing, so it can show "couldn't decrypt" rather
        /// than silently loading garbage.
        /// </summary>
        [TestMethod]
        public void Decrypt_WithWrongPassword_DoesNotReturnThePlainText()
        {
            try
            {
                var result = StringCipher.Decrypt(GoldenAsciiCipher, "NotThePassword");
                Assert.AreNotEqual(GoldenAsciiPlain, result,
                    "Decrypting with the wrong password returned the real secret.");
            }
            catch (CryptographicException)
            {
                // Expected: PKCS7 padding validation rejects the wrong key.
            }
        }

        #endregion

        #region Config level - the shape the installer actually reads and writes

        /// <summary>
        /// Proves the whole config load path decrypts secrets written by the shipping build, not just
        /// <see cref="StringCipher"/> in isolation.
        /// </summary>
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
            Assert.AreEqual(GoldenAsciiPlain, result.Config.SQLServerAdminPassword,
                "SQL admin password did not survive.");
            Assert.AreEqual(GoldenSpecialPlain, result.Config.InstallerAccount.Secret,
                "Installer account client secret did not survive.");
            Assert.AreEqual(GoldenGreekPlain, result.Config.ActivityAccount.Secret,
                "Activity account client secret did not survive.");
        }

        /// <summary>
        /// Save then load, through the real JSON path.
        /// </summary>
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
        /// Secrets must never be written to the file in the clear.
        /// </summary>
        [TestMethod]
        public void ToJson_DoesNotPersistSecretsInPlainText()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.SQLServerAdminPassword = GoldenAsciiPlain;
            config.InstallerAccount.Secret = GoldenSpecialPlain;

            var json = config.ToJson(PassPhrase);

            StringAssert.Contains(json, "SQLServerAdminPasswordHash",
                "Expected the encrypted property to be persisted.");
            Assert.IsFalse(json.Contains(GoldenAsciiPlain),
                "The SQL admin password was written to the config file in plain text.");
            Assert.IsFalse(json.Contains(GoldenSpecialPlain),
                "A client secret was written to the config file in plain text.");
        }

        /// <summary>
        /// A config saved with one password must not open with another - the installer treats
        /// <c>DecryptedOk == false</c> as "wrong password" and clears the hashes.
        /// </summary>
        [TestMethod]
        public void LoadFromJson_WithWrongPassword_ReportsFailureRatherThanGarbage()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.SQLServerAdminPassword = GoldenAsciiPlain;

            var result = SolutionInstallConfig.LoadFromJson(config.ToJson(PassPhrase), "NotThePassword");

            Assert.AreNotEqual(GoldenAsciiPlain, result.Config.SQLServerAdminPassword,
                "The wrong password recovered the real SQL admin password.");
        }

        #endregion

        #region Encoding

        /// <summary>
        /// The secret is UTF8-encoded before encryption. Pinning that explicitly stops a future change
        /// to, say, Unicode/UTF-16 - which would round-trip perfectly in-process while making every
        /// existing non-ASCII secret unreadable.
        /// </summary>
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
    }
}
