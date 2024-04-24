using Common.DataUtils.Sql;
using Microsoft.Graph;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.User.UserApps
{
    public class UserAppsLoadState<APPTYPE>
    {
        public bool Retry { get; set; }
        public List<APPTYPE> Results { get; set; } = new List<APPTYPE>();
    }

    /// <summary>
    /// Thrown specifically when permissions aren't granted to user apps collection
    /// </summary>
    public class AccessDeniedToTeamsAppsException : Exception { }
    public class UserNotFoundException : Exception { }


    internal class UserBatchRequestKeyPair
    {
        public string BatchRequestId { get; set; } = string.Empty;
        public string EmailAddress { get; set; } = string.Empty;
    }

    [TempTableName(GraphAndSqlUserAppLoader.STAGING_TABLE_NAME)]
    internal class UserAppLogTempEntity
    {
        public UserAppLogTempEntity(TeamsAppDefinition teamsAppDefinition, string email)
        {
            this.AppName = teamsAppDefinition.DisplayName;
            this.AppDefinitionId = teamsAppDefinition.TeamsAppId;
            this.UserEmail = email;
        }

        [Column("email")]
        public string UserEmail { get; set; }

        [Column("app_def_id")]
        public string AppDefinitionId { get; set; }

        [Column("app_name")]
        public string AppName { get; set; }

    }
}
