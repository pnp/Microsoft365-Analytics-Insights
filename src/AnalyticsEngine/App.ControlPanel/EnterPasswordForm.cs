using System;
using System.Drawing;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class EnterPasswordForm : Form
    {
        public EnterPasswordForm()
        {
            InitializeComponent();
            ShowValidationError = message => MessageBox.Show(this, message, "Enter Password", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        }

        const int MIN_PWD_LEN = 6;

        public string Password { get; set; }

        /// <summary>
        /// Ask for the password twice, for a file about to be encrypted with it. A typing mistake there isn't noticed until
        /// the file is opened again, when nothing can decrypt the secrets in it. Opening a file asks once.
        /// </summary>
        public bool ConfirmPassword { get; set; }

        /// <summary>Says why the password can't be used. Shows a message box.</summary>
        public Action<string> ShowValidationError { get; set; }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (txtPassword.Text.Length < MIN_PWD_LEN)
            {
                ShowValidationError($"Minimum password length is {MIN_PWD_LEN} characters");
                return;
            }
            if (ConfirmPassword && txtConfirmPassword.Text != txtPassword.Text)
            {
                ShowValidationError("The two passwords don't match. Enter the same password in both boxes.");
                txtConfirmPassword.Focus();
                return;
            }

            this.Password = txtPassword.Text;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void EnterPasswordForm_Load(object sender, EventArgs e)
        {
            if (!ConfirmPassword)
            {
                // Give back the space the second box takes. Measured, so it is right at any DPI.
                var confirmRowHeight = txtConfirmPassword.Bottom - txtPassword.Bottom;
                lblConfirmPassword.Visible = false;
                txtConfirmPassword.Visible = false;
                ClientSize = new Size(ClientSize.Width, ClientSize.Height - confirmRowHeight);
            }
#if DEBUG
            txtPassword.Text = "Corp123!";
            txtConfirmPassword.Text = txtPassword.Text;
#endif
        }

        private void chkShowPassword_CheckedChanged(object sender, EventArgs e)
        {
            txtPassword.PasswordChar = !chkShowPassword.Checked ? '*' : '\0';
            txtConfirmPassword.PasswordChar = txtPassword.PasswordChar;
        }
    }
}
