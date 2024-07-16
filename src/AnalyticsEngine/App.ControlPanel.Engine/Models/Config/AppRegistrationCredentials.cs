using DataUtils;
using System.Collections.Generic;

namespace App.ControlPanel.Engine.Entities
{

    /// <summary>
    /// Used to call Azure API. Setup via https://docs.microsoft.com/en-gb/azure/active-directory/develop/howto-create-service-principal-portal
    /// </summary>
    public class AppRegistrationCredentials
    {
        #region Constructors

        public AppRegistrationCredentials()
        {
            this.ClientId = string.Empty;
            this.Secret = string.Empty;
            this.DirectoryId = string.Empty;
        }
        public AppRegistrationCredentials(string clientId, string secret, string dirId) : this()
        {
            this.ClientId = clientId;
            this.Secret = secret;
            this.DirectoryId = dirId;
        }

        #endregion

        #region Props

        public string ClientId { get; set; }
        public string DirectoryId { get; set; }

        [Newtonsoft.Json.JsonIgnore]
        public string Secret { get; set; }

        public string SecretHash { get; set; }

        public List<string> GetValidationErrors()
        {
            List<string> errs = new List<string>();
            if (string.IsNullOrWhiteSpace(this.ClientId))
            {
                errs.Add("Provide an Azure client ID.");
            }
            if (string.IsNullOrWhiteSpace(this.Secret))
            {
                errs.Add("Provide an Azure client secret.");
            }
            if (string.IsNullOrWhiteSpace(this.DirectoryId))
            {
                errs.Add("Provide an Azure directory ID.");
            }

            return errs;
        }

        #endregion

        internal void DecryptSecretFromLoadedHashProperty(string password)
        {
            if (!string.IsNullOrEmpty(this.SecretHash))
                this.Secret = StringCipher.Decrypt(this.SecretHash, password);
            else
                this.Secret = string.Empty;
        }

        internal void EncryptSecretToHashProperty(string password)
        {
            if (!string.IsNullOrEmpty(this.Secret))
            {
                this.SecretHash = StringCipher.Encrypt(this.Secret, password);
            }
            else
            {
                this.SecretHash = string.Empty;
            }
        }
    }
}
