namespace LazerRender.Api.Configuration;

/// <summary>
/// Filesystem layout for the service. All paths are optional; empty values resolve to
/// <c>{contentRoot}/data/...</c> subdirectories. The Realm directory is the persistent
/// LazerRender storage passed to <c>--storage</c>.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DataDirectory { get; set; } = "";
    public string UploadsDirectory { get; set; } = "";
    public string JobsDirectory { get; set; } = "";
    public string ResultsDirectory { get; set; } = "";
    public string RealmDirectory { get; set; } = "";
}
