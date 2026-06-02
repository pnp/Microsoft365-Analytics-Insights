using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;

namespace WebJob.Office365ActivityImporter.Engine.Entities
{
    public class UserReaction
    {
        public User GraphUser { get; set; } = new User();
        public string Reaction { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public DateTime When { get; set; } = DateTime.MinValue;
    }
}
