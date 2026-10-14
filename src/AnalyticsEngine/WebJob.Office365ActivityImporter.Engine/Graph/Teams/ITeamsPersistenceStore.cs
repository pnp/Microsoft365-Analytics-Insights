using Common.Entities.Entities;
using Common.Entities.Teams;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Writes one Team using the existing lookup cache and SQL save boundaries.
    /// Lookup methods may implicitly flush new Teams/users, exactly as before; SaveChanges
    /// completes the Team before its pending delta checkpoints may be written.
    /// </summary>
    public interface ITeamsPersistenceStore
    {
        Task<TeamDefinition> GetOrCreateTeam(O365Team team);
        Task SaveOwners(List<Microsoft.Graph.Models.User> owners, TeamDefinition team);
        Task SaveMembers(List<BaseUser> members, O365Team team);
        Task<TeamChannel> SaveChannel(ChannelWithReactions channel, TeamDefinition team);
        Task SaveStats(MessageCognitiveStats stats, TeamDefinition team);
        Task SaveReactions(List<UserReaction> reactions);
        Task SaveChanges(O365Team source, TeamDefinition team);
    }
}
