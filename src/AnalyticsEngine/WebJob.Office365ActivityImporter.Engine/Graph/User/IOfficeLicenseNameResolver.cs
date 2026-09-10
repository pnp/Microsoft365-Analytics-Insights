namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Resolves Microsoft SKU part numbers to the display names stored in <c>dbo.license_types</c>.
    /// </summary>
    public interface IOfficeLicenseNameResolver
    {
        string GetDisplayNameFor(string id);
    }
}
