using System.Windows.Forms;

namespace App.ControlPanel.Controls
{
    public class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
        }
    }
}
