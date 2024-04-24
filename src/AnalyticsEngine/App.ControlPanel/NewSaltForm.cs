using Common.DataUtils;
using System;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class NewSaltForm : Form
    {
        public NewSaltForm()
        {
            InitializeComponent();
        }

        private void NewSalt_Load(object sender, EventArgs e)
        {

            // Generate salt for PII
            string salt = StringUtils.GenerateNewSalt();
            txtSalt.Text = salt;
        }

        private void btnCopy_Click(object sender, EventArgs e)
        {
            Clipboard.SetText(txtSalt.Text);
            MessageBox.Show("Copied to clipboard", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
