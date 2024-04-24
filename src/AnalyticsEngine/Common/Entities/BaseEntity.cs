using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities
{
    /// <summary>
    /// Base implementation for all EF classes. Not fully rolled out to all classes yet.
    /// </summary>
    public abstract class AbstractEFEntity
    {
        [Key]
        [Column("id")]
        public int ID { get; set; }

        public bool IsSavedToDB
        {
            get { return this.ID != 0; }
        }


        public override string ToString()
        {
            return $"{this.GetType().Name}: {nameof(ID)}={ID}";
        }
    }

    /// <summary>
    /// Base implementation for all EF classes with name field
    /// </summary>
    public abstract class AbstractEFEntityWithName : AbstractEFEntity
    {
        [Column("name")]
        [MaxLength(100)]
        public string Name { get; set; }

        public override string ToString()
        {
            return $"{base.ToString()},{nameof(Name)}={Name}";
        }
    }

}
