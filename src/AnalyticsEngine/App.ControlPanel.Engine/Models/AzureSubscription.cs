using System;

namespace App.ControlPanel.Engine.Entities
{
    public class AzureSubscription
    {
        public AzureSubscription()
        {
            this.DisplayName = string.Empty;
            this.SubId = string.Empty;
        }
        public AzureSubscription(string subscriptionId, string displayName) : this()
        {
            this.SubId = subscriptionId;
            this.DisplayName = displayName;
        }

        public string DisplayName { get; set; }
        public string SubId { get; set; }

        public override string ToString()
        {
            return DisplayName;
        }

        [Newtonsoft.Json.JsonIgnore]
        public bool IsValidSubscription
        {
            get
            {
                Guid subId = new Guid();
                return !(string.IsNullOrEmpty(this.DisplayName)) && !(string.IsNullOrEmpty(this.SubId) && Guid.TryParse(this.SubId, out subId));
            }
        }
    }
}
