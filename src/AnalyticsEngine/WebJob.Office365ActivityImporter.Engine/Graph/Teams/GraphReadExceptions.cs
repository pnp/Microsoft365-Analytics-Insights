using System;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    public class ChannelMessagesReadException : Exception
    {
        public ChannelMessagesReadException(Exception inner) : base("Couldn't read channel messages. Check inner exception", inner) { }
    }
}
