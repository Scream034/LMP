using MyLiteMusicPlayer.UI;

// Оборачиваем в try-catch для отлова критических ошибок при запуске
try
{
    using var app = new MainWindow();
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"Critical Error: {ex.Message}");
}