using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace App.ControlPanel.Engine.Models
{



    // NOTE: Generated code may require at least .NET Framework 4.5 or .NET Core/Standard 2.0.
    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    [System.Xml.Serialization.XmlRootAttribute(Namespace = "", IsNullable = false)]
    public partial class publishData
    {

        public static publishData FromXml(StreamReader reader)
        {
            var xml = reader.ReadToEnd();
            return FromXml(xml);
        }
        public static publishData FromXml(string xml)
        {
            var serializer = new XmlSerializer(typeof(publishData));
            using (var r = new StringReader(xml))
            {
                var result = (publishData)serializer.Deserialize(r);
                return result;
            }
        }

        public FtpPublishInfo GetPublishFtpsUrl()
        {

            var ftpProfile = publishProfile.Where(p => p.publishMethod == "FTP").FirstOrDefault();
            if (ftpProfile != null)
            {
                return new FtpPublishInfo { RootUrl = ftpProfile.publishUrl, Username = ftpProfile.userName, Password = ftpProfile.userPWD };
            }
            else
            {
                throw new Exception("Can't find FTP publishing method");
            }
        }

        private publishDataPublishProfile[] publishProfileField;

        /// <remarks/>
        [System.Xml.Serialization.XmlElementAttribute("publishProfile")]
        public publishDataPublishProfile[] publishProfile
        {
            get
            {
                return this.publishProfileField;
            }
            set
            {
                this.publishProfileField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public partial class publishDataPublishProfile
    {

        private publishDataPublishProfileDatabases databasesField;

        private string profileNameField;

        private string publishMethodField;

        private string publishUrlField;

        private string msdeploySiteField;

        private string userNameField;

        private string userPWDField;

        private string destinationAppUrlField;

        private string sQLServerDBConnectionStringField;

        private string mySQLDBConnectionStringField;

        private string hostingProviderForumLinkField;

        private string controlPanelLinkField;

        private string webSystemField;

        private string targetDatabaseEngineTypeField;

        private string targetServerVersionField;

        private string ftpPassiveModeField;

        /// <remarks/>
        public publishDataPublishProfileDatabases databases
        {
            get
            {
                return this.databasesField;
            }
            set
            {
                this.databasesField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string profileName
        {
            get
            {
                return this.profileNameField;
            }
            set
            {
                this.profileNameField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string publishMethod
        {
            get
            {
                return this.publishMethodField;
            }
            set
            {
                this.publishMethodField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string publishUrl
        {
            get
            {
                return this.publishUrlField;
            }
            set
            {
                this.publishUrlField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string msdeploySite
        {
            get
            {
                return this.msdeploySiteField;
            }
            set
            {
                this.msdeploySiteField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string userName
        {
            get
            {
                return this.userNameField;
            }
            set
            {
                this.userNameField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string userPWD
        {
            get
            {
                return this.userPWDField;
            }
            set
            {
                this.userPWDField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string destinationAppUrl
        {
            get
            {
                return this.destinationAppUrlField;
            }
            set
            {
                this.destinationAppUrlField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string SQLServerDBConnectionString
        {
            get
            {
                return this.sQLServerDBConnectionStringField;
            }
            set
            {
                this.sQLServerDBConnectionStringField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string mySQLDBConnectionString
        {
            get
            {
                return this.mySQLDBConnectionStringField;
            }
            set
            {
                this.mySQLDBConnectionStringField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string hostingProviderForumLink
        {
            get
            {
                return this.hostingProviderForumLinkField;
            }
            set
            {
                this.hostingProviderForumLinkField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string controlPanelLink
        {
            get
            {
                return this.controlPanelLinkField;
            }
            set
            {
                this.controlPanelLinkField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string webSystem
        {
            get
            {
                return this.webSystemField;
            }
            set
            {
                this.webSystemField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string targetDatabaseEngineType
        {
            get
            {
                return this.targetDatabaseEngineTypeField;
            }
            set
            {
                this.targetDatabaseEngineTypeField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string targetServerVersion
        {
            get
            {
                return this.targetServerVersionField;
            }
            set
            {
                this.targetServerVersionField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string ftpPassiveMode
        {
            get
            {
                return this.ftpPassiveModeField;
            }
            set
            {
                this.ftpPassiveModeField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public partial class publishDataPublishProfileDatabases
    {

        private publishDataPublishProfileDatabasesAdd addField;

        /// <remarks/>
        public publishDataPublishProfileDatabasesAdd add
        {
            get
            {
                return this.addField;
            }
            set
            {
                this.addField = value;
            }
        }
    }

    /// <remarks/>
    [System.SerializableAttribute()]
    [System.ComponentModel.DesignerCategoryAttribute("code")]
    [System.Xml.Serialization.XmlTypeAttribute(AnonymousType = true)]
    public partial class publishDataPublishProfileDatabasesAdd
    {

        private string nameField;

        private string connectionStringField;

        private string providerNameField;

        private string typeField;

        private string targetDatabaseEngineTypeField;

        private string targetServerVersionField;

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string name
        {
            get
            {
                return this.nameField;
            }
            set
            {
                this.nameField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string connectionString
        {
            get
            {
                return this.connectionStringField;
            }
            set
            {
                this.connectionStringField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string providerName
        {
            get
            {
                return this.providerNameField;
            }
            set
            {
                this.providerNameField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string type
        {
            get
            {
                return this.typeField;
            }
            set
            {
                this.typeField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string targetDatabaseEngineType
        {
            get
            {
                return this.targetDatabaseEngineTypeField;
            }
            set
            {
                this.targetDatabaseEngineTypeField = value;
            }
        }

        /// <remarks/>
        [System.Xml.Serialization.XmlAttributeAttribute()]
        public string targetServerVersion
        {
            get
            {
                return this.targetServerVersionField;
            }
            set
            {
                this.targetServerVersionField = value;
            }
        }
    }


}
