using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Forms;

namespace App.ControlPanel.Controls
{
    public partial class FileSelection : UserControl
    {
        public FileSelection()
        {
            InitializeComponent();
        }
        string _label = string.Empty;
        [DefaultValue("File")]
        public string Label
        {
            get
            {
                return _label;
            }
            set
            {
                _label = value;
            }
        }

        [Browsable(false)]
        public bool IsValidFile
        {
            get
            {
                return File.Exists(txtFile.Text);
            }
        }


        [Browsable(false)]
        public string SelectedFileName
        {
            get
            {
                return txtFile.Text;
            }
            set
            {
                txtFile.Text = value;
            }
        }

        private void FileSelection_Load(object sender, EventArgs e)
        {
            lblLabel.Text = this.Label;
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            openFileDialog1.Title = $"Select {this.Label}";
            if (openFileDialog1.ShowDialog() == DialogResult.OK)
            {
                txtFile.Text = openFileDialog1.FileName;
            }
        }
    }
}
