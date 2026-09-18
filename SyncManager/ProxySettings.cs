namespace SyncManager;

public class ProxySettings
{
    public string UserHeader { get; set; } = "X-Remote-User";
    public string GroupHeader { get; set; } = "X-Remote-Group";
    public string GroupSeparator { get; set; } = ";";
}