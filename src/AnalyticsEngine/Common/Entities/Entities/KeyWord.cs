using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities
{
    /// <summary>
    /// Used in Teams channel analysis (for now)
    /// </summary>
    [Table("keywords")]
    public class KeyWord : AbstractEFEntityWithName
    {
    }
}
