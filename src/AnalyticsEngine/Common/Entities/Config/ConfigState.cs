using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;

namespace Common.Entities.Config
{
    [Table("sys_configs")]
    public class ConfigState : AbstractEFEntity
    {

        [Column("config_json")]
        public string ConfigJson { get; set; }

        /// <summary>
        /// When the installer saved this configuration, in the local time of the machine it ran on. Not an order: see
        /// <see cref="ConfigStateQueries.LatestSavedFirst"/>.
        /// </summary>
        [Column("date_applied")]
        public DateTime DateApplied { get; set; }

        [Column("installed_by_username")]
        public string InstalledByUser { get; set; }

        [Column("messages")]
        public string Messages { get; set; }
    }

    public static class ConfigStateQueries
    {
        /// <summary>
        /// The saved configurations, the one saved last first.
        /// </summary>
        /// <remarks>
        /// Ordered by <c>id</c>, the identity each save takes in turn, not by <see cref="ConfigState.DateApplied"/>. That is
        /// the local time of whichever machine ran the installer, so a configuration saved later from a machine in a time
        /// zone further west can carry an earlier time than the one it replaced - and an opt-out from usage reporting saved
        /// that way was ignored.
        /// </remarks>
        public static IOrderedQueryable<ConfigState> LatestSavedFirst(this IQueryable<ConfigState> configs)
        {
            return configs.OrderByDescending(c => c.ID);
        }
    }
}
