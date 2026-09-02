using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Read port for a single Teams call record. Extracted so the calls import logic no longer depends
    /// on a Graph client. See issue #378.
    /// </summary>
    public interface ICallRecordSourceLoader
    {
        /// <summary>
        /// Load a call record and resolve its participants' email addresses, or return <c>null</c> when
        /// the call can't be read (Graph returned nothing usable).
        /// </summary>
        Task<CallRecordDTO> LoadCallRecord(string callId);
    }

    /// <summary>
    /// Write port for Teams call records. Extracted so the calls import logic no longer depends on
    /// Entity Framework. See issue #378.
    /// </summary>
    public interface ICallRecordPersistenceManager
    {
        /// <summary>
        /// Persist a call record, replacing any previously imported record with the same Graph id.
        /// </summary>
        Task SaveOrReplaceCallRecord(CallRecordDTO call);
    }
}
