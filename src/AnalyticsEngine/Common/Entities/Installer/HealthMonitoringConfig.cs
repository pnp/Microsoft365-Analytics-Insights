using System;
using System.Collections.Generic;
using System.Net.Mail;

namespace Common.Entities.Installer
{
    public class HealthMonitoringConfig : BaseConfig
    {
        // Existing configurations do not acquire billable alert rules without an administrator opting in.
        public bool Enabled { get; set; }
        public string NotificationEmail { get; set; } = string.Empty;
        public int MissingHeartbeatMinutes { get; set; } = 15;

        public override List<string> ValidatInputAndGetErrors()
        {
            var errors = new List<string>();
            if (!Enabled) return errors;

            if (MissingHeartbeatMinutes < 15 || MissingHeartbeatMinutes > 1440)
                errors.Add("Health monitoring: the missing-heartbeat threshold must be between 15 and 1440 minutes.");

            try
            {
                var address = new MailAddress(NotificationEmail ?? string.Empty);
                if (!string.Equals(address.Address, NotificationEmail?.Trim(), StringComparison.Ordinal))
                    errors.Add("Health monitoring: enter one notification email address, without a display name.");
            }
            catch (FormatException)
            {
                errors.Add("Health monitoring: enter a valid notification email address or disable alert provisioning.");
            }
            catch (ArgumentException)
            {
                errors.Add("Health monitoring: enter a notification email address or disable alert provisioning.");
            }
            return errors;
        }
    }
}
