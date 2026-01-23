namespace MyLiteMusicPlayer.Core.Models;

public class AppSecrets
{
    public GoogleSeсrets Google { get; set; } = new();
}

public class GoogleSeсrets
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
