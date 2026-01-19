namespace MyLiteMusicPlayer.UI.Tabs;

public interface ITab
{
    string Id { get; }
    string Name { get; }
    bool CanClose { get; }
    void Render();
    void OnOpen() { }
    void OnClose() { }
}