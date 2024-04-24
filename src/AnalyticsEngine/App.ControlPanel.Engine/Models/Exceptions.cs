using System;

namespace App.ControlPanel.Engine.Entities
{
    public class SPOInsightsException : Exception
    {
        public SPOInsightsException(string msg) : base(msg) { }
    }

    public class InvalidFormInputException : SPOInsightsException
    {
        public InvalidFormInputException(string msg) : base(msg) { }
    }

    public class UnexpectedInstallException : SPOInsightsException
    {
        public UnexpectedInstallException(string msg) : base(msg) { }
    }
}
