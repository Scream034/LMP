using MyLiteMusicPlayer.UI;

namespace MyLiteMusicPlayer;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Настройка для высокого DPI
        if (OperatingSystem.IsWindows())
        {
            SetProcessDPIAware();
        }
        
        try
        {
            using var app = new MainWindow();
            app.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Critical Error: {ex}");
            
            // Записываем в лог файл
            try
            {
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LiteMusicPlayer", "crash.log");
                
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.WriteAllText(logPath, $"[{DateTime.Now}]\n{ex}\n\n");
            }
            catch { }
            
            Environment.Exit(1);
        }
    }
    
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();
}