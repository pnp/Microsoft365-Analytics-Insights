using App.ControlPanel.Engine.Entities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;

namespace App.ControlPanel.Controls
{
    public partial class AppLoginDetailsControl : UserControl
    {
        public AppLoginDetailsControl()
        {
            InitializeComponent();
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public AppRegistrationCredentials FormCredentials
        {
            get
            {
                if (!ValidateFields(false)) throw new InvalidFormInputException("Form isn't valid");
                return new AppRegistrationCredentials(txtAzureClientId.Text, txtAzureClientSecret.Text, txtAzureSubId.Text);
            }
            set
            {
                if (value != null)
                {
                    txtAzureClientId.Text = value.ClientId;
                    txtAzureClientSecret.Text = value.Secret;
                    txtAzureSubId.Text = value.DirectoryId;
                }
                else
                {
                    txtAzureClientId.Text = string.Empty;
                    txtAzureClientSecret.Text = string.Empty;
                    txtAzureSubId.Text = string.Empty;
                }
            }
        }

        /// <summary>
        /// Used for error messages "No client secret for X"
        /// </summary>
        [DefaultValue("App reg1")]
        public string ContextName { get; set; }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool HasValidFields
        {
            get { return ValidateFields(false); }
        }
        public bool ValidateFields(bool showErrors)
        {
            var t = new AppRegistrationCredentials(txtAzureClientId.Text, txtAzureClientSecret.Text, txtAzureSubId.Text);

            List<string> errs = t.GetValidationErrors();

            if (errs.Count > 0)
            {
                if (showErrors)
                {
                    CommonUIThings.ShowValidationErrors(errs);
                }
                return false;
            }
            return true;
        }


    }
}
