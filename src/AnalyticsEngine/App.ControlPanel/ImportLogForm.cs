using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class ImportLogForm : Form
    {
        public ImportLogForm()
        {
            InitializeComponent();
        }

        private void ImportLogForm_Load(object sender, EventArgs e)
        {
        }

        public void SetDisplay(import_log log, string message)
        {
            if (log.event_id.HasValue)
            {
                txtEventID.Text = log.event_id.ToString();
            }
            else
            {
                txtEventID.Text = "No event data";
            }

            if (log.hit_id.HasValue)
            {
                txtHitID.Text = log.hit_id.ToString();
            }
            else
            {
                txtHitID.Text = "No hit data";
            }

            txtTimestamp.Text = log.time_stamp.ToString();
            this.Text = $"Import Log Details - Searching for '{message}'";

            // Format nicely
            JToken parsedJson = JToken.Parse(log.contents);
            var beautified = parsedJson.ToString(Formatting.Indented);
            rtbContent.Text = beautified;

            // Highlight all instances of search text
            bool moreSelections = true; int lastSearch = 0;
            while (moreSelections)
            {
                int searchStart = rtbContent.Find(message, lastSearch, RichTextBoxFinds.None);
                if (searchStart > -1)
                {
                    rtbContent.Select(searchStart, message.Length);
                    rtbContent.SelectionBackColor = Color.Red;
                    rtbContent.SelectionColor = Color.White;
                    lastSearch = searchStart + message.Length;
                }
                else
                {
                    moreSelections = false;
                }
            }
            rtbContent.Select(0, 0);
        }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}
