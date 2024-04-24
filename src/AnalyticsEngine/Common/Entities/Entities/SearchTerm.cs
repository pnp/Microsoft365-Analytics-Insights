using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// A term searched for. Lookup for "search"
    /// </summary>
    /// 
    [Table("search_terms")]
    public class SearchTerm : AbstractEFEntity
    {
        public SearchTerm()
        {
        }

        public string search_term { get; set; }

    }
}
