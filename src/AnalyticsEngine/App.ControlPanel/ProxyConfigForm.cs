using App.ControlPanel.Engine.Entities;
using System;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class ProxyConfigForm : Form
    {
        public ProxyConfigForm()
        {
            InitializeComponent();
        }

        public InstallerFtpConfig FtpConfig
        {
            get
            {
                int port = 0;
                int.TryParse(txtFtpProxyPort.Text, out port);

                return new InstallerFtpConfig()
                {
                    UseFTPS = chkUseFTPS.Checked,
                    UsePassive = chkFtpPassiveMode.Checked,
                    ProxyHost = txtFtpProxyHost.Text,
                    UseFtpProxy = chkFtpProxy.Checked,
                    ProxyPassword = txtFtpProxyPassword.Text,
                    ProxyUsername = txtFtpProxyUsername.Text,
                    ProxyPort = port,
                    IntegratedAuth = false
                };
            }
            set
            {
                chkFtpPassiveMode.Checked = value.UsePassive;
                chkFtpProxy.Checked = value.UseFtpProxy;
                txtFtpProxyHost.Text = value.ProxyHost;
                txtFtpProxyPassword.Text = value.ProxyPassword;
                txtFtpProxyPort.Text = value.ProxyPort.ToString();
                txtFtpProxyUsername.Text = value.ProxyUsername;
                opUsernameAndPassword.Checked = !value.IntegratedAuth;
            }
        }

        private void chkFtpProxy_CheckedChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void UpdateResponsiveUIControls()
        {
            grpProxy.Enabled = FtpConfig.UseFtpProxy;
            chkFtpProxy.Checked = FtpConfig.UseFtpProxy;
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            if (!FtpConfig.IsValid)
            {
                MessageBox.Show("Invalid FTP proxy configuration", "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        private void ProxyConfigForm_Load(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void opAuth_CheckedChanged(object sender, EventArgs e)
        {
            txtFtpProxyUsername.Enabled = opUsernameAndPassword.Checked;
            txtFtpProxyPassword.Enabled = opUsernameAndPassword.Checked;
        }

    }
}
