namespace Common.Entities.Migrations
{
    using System.CodeDom.Compiler;
    using System.Data.Entity.Migrations.Infrastructure;
    using System.Resources;

    [GeneratedCode("EntityFramework.Migrations", "6.5.2")]
    public sealed partial class AddCopilotCostPreview : IMigrationMetadata
    {
        private readonly ResourceManager Resources = new ResourceManager(typeof(AddCopilotCostPreview));
        string IMigrationMetadata.Id => "202610230900001_AddCopilotCostPreview";
        string IMigrationMetadata.Source => null;
        string IMigrationMetadata.Target => Resources.GetString("Target");
    }
}
