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

        public InstallerProxyConfig ProxyConfig
        {
            get
            {
                int port = 0;
                int.TryParse(txtProxyPort.Text, out port);

                return new InstallerProxyConfig
                {
                    Host = txtProxyHost.Text,
                    UseProxy = chkProxy.Checked,
                    Password = txtProxyPassword.Text,
                    Username = txtProxyUsername.Text,
                    Port = port,
                    IntegratedAuth = !chkBasicAuthentication.Checked
                };
            }
            set
            {
                chkProxy.Checked = value.UseProxy;
                txtProxyHost.Text = value.Host;
                txtProxyPassword.Text = value.Password;
                txtProxyPort.Text = value.Port.ToString();
                txtProxyUsername.Text = value.Username;
                chkBasicAuthentication.Checked = !value.IntegratedAuth;
            }
        }

        private void chkProxy_CheckedChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void UpdateResponsiveUIControls()
        {
            grpProxy.Enabled = ProxyConfig.UseProxy;
            chkProxy.Checked = ProxyConfig.UseProxy;
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            if (!ProxyConfig.IsValid)
            {
                MessageBox.Show("Invalid deployment proxy configuration", "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            txtProxyUsername.Enabled = chkBasicAuthentication.Checked;
            txtProxyPassword.Enabled = chkBasicAuthentication.Checked;
        }

    }
}
