using App.ControlPanel.Engine.Entities;
using System.ComponentModel;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class SystemCredentialsControl : UserControl
    {
        public SystemCredentialsControl()
        {
            InitializeComponent();
        }

        public bool InstallerAccountHasValidFields => installAccountControl.HasValidFields;
        public bool RuntimeAccountHasValidFields => runtimeO365AccountDetails.HasValidFields;

        [Browsable(false)]
        public AppRegistrationCredentials InstallerAccount
        {
            get { return installAccountControl.FormCredentials; }
            set { installAccountControl.FormCredentials = value; }
        }

        [Browsable(false)]
        public AppRegistrationCredentials RuntimeAccount
        {
            get { return runtimeO365AccountDetails.FormCredentials; }
            set { runtimeO365AccountDetails.FormCredentials = value; }
        }
    }
}
