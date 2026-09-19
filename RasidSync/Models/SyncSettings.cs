namespace RasidSync.Models;

public sealed class SyncSettings
{
    public string ApiUrl { get; set; } = "http://localhost:5263";
    public string LocalServer { get; set; } = "localhost";
    public uint LocalPort { get; set; } = 3306;
    public string LocalDatabase { get; set; } = string.Empty;
    public string LocalUsername { get; set; } = "root";
    public string LocalPassword { get; set; } = string.Empty;
    public string OnlineDatabase { get; set; } = string.Empty;
    public string DeviceUuid { get; set; } = Guid.NewGuid().ToString();
}
