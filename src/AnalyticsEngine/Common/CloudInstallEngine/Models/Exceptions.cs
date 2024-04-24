using System;

namespace CloudInstallEngine.Models
{
    public class InstallException : Exception
    {
        public InstallException(string message) : base(message)
        {
        }
    }
}
