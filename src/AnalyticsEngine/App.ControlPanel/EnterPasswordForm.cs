using System;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class EnterPasswordForm : Form
    {
        public EnterPasswordForm()
        {
            InitializeComponent();
        }

        const int MIN_PWD_LEN = 6;

        public string Password { get; set; }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (txtPassword.Text.Length < MIN_PWD_LEN)
            {
                MessageBox.Show($"Minimum password length is {MIN_PWD_LEN} characters", "Password Length", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                return;
            }

            this.Password = txtPassword.Text;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void EnterPasswordForm_Load(object sender, EventArgs e)
        {
#if DEBUG
            txtPassword.Text = "Corp123!";
#endif
        }

        private void chkShowPassword_CheckedChanged(object sender, EventArgs e)
        {
            txtPassword.PasswordChar = !chkShowPassword.Checked ? '*' : '\0';

        }
    }
}
