using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public class CommonUIThings
    {
        internal static void ShowValidationErrors(List<string> errs)
        {

            string msg = string.Empty;
            foreach (var err in errs)
            {
                msg += Environment.NewLine + $"-{err}";
            }

            MessageBox.Show($"Validation errors: {msg}", "Can't Continue", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
