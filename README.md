# 🎵 Lite Music Player (LMP)

**Легковесный клиент YouTube Music, который не жрёт гигабайт оперативки.**

Официальный YTM на Windows — 600–1500 МБ RAM даже когда просто открыт.  
Этот плеер — **250–350 МБ в среднем**.

![Project Status](https://img.shields.io/badge/Status-Active-brightgreen)
![Platform](https://img.shields.io/badge/Platform-Windows-blue)
![Tech](https://img.shields.io/badge/Tech-Avalonia%20|%20.NET%2010%20|%20VLC-blueviolet)

---

## ✨ Особенности

- 🚀 **Экстремальная производительность** — .NET 10 + Avalonia UI, оптимизирован для слабых ПК
- 🎧 **Умный стриминг (Memory-First)** — мгновенный старт воспроизведения с параллельным кешированием
- 🖼️ **Spotify-inspired UI** — эстетичный интерфейс с плавными анимациями
- 📦 **Агрессивное кеширование** — изображения, поиск, аудио сохраняются локально
- 🔊 **Продвинутый звук** — LibVLC с Gain Control и плавным переключением треков
- 🌍 **Локализация** — русский и английский языки
- 📚 **Личная библиотека** — плейлисты, лайки, история (офлайн)

---

## 🛠 Технологии

| Компонент | Технология |
|-----------|------------|
| UI Framework | [Avalonia UI](https://avaloniaui.net/) (MVVM, ReactiveUI) |
| Audio Engine | [LibVLCSharp](https://github.com/videolan/libvlcsharp) |
| Database | SQLite + Entity Framework Core |
| DI | Microsoft.Extensions.DependencyInjection |
| Images | AsyncImageLoader + кастомный дисковый кеш |

---

## 🔧 Сборка проекта

### Требования

| Требование | Версия | Примечание |
|------------|--------|------------|
| .NET SDK | **10.0+** | [Скачать](https://dotnet.microsoft.com/download/dotnet/10.0) (Preview) |
| OS | Windows 10/11 | x64 |
| RAM | 4+ GB | Рекомендуется |

### Быстрый старт

```bash
# Клонировать репозиторий
git clone https://github.com/Scream034/LiteYTMusicPlayer.git
cd LiteYTMusicPlayer

# Запустить (автоматически восстановит зависимости)
dotnet run --project LMP.csproj
```

### Сборка через скрипты

В корне проекта есть готовые `.bat` файлы:

| Скрипт | Описание |
|--------|----------|
| `build-debug.bat` | Быстрая сборка для отладки |
| `build-release.bat` | Оптимизированная Release сборка |
| `publish.bat` | Полная публикация (self-contained) |
| `clean.bat` | Очистка bin/obj папок |

```bash
# Примеры использования
build-debug.bat      # Собрать Debug
build-release.bat    # Собрать Release  
publish.bat          # Создать готовый дистрибутив в ./publish
clean.bat            # Очистить проект
```

### Настройка IDE

<details>
<summary><b>Visual Studio 2022</b></summary>

1. Установить workload ".NET Desktop Development"
2. Установить расширение "Avalonia for Visual Studio"
3. Открыть `LMP.csproj`
4. F5 для запуска

</details>

<details>
<summary><b>JetBrains Rider</b></summary>

1. Установить плагин "AvaloniaRider"
2. Открыть папку проекта
3. Shift+F10 для запуска

</details>

<details>
<summary><b>VS Code</b></summary>

1. Установить расширения: C# Dev Kit, Avalonia for VS Code
2. Открыть папку проекта
3. F5 для отладки (конфигурация уже настроена)

</details>

### Решение проблем

<details>
<summary><b>❌ ".NET 10 not found"</b></summary>

```bash
# Проверить установленные SDK
dotnet --list-sdks

# Скачать .NET 10: https://dotnet.microsoft.com/download/dotnet/10.0
```

</details>

<details>
<summary><b>❌ Ошибки сборки</b></summary>

```bash
# Полная очистка и пересборка
clean.bat
dotnet restore --force
build-debug.bat
```

</details>

---

## 📈 Статус разработки

### ✅ Готово

- [x] Воспроизведение треков (LibVLC)
- [x] Умный стриминг с кешированием
- [x] Поиск треков/видео
- [x] Кеширование (изображения, поиск, аудио)
- [x] Локализация (RU/EN)
- [x] История прослушиваний
- [x] Система лайков
- [x] Плейлисты
- [x] Синхронизация через Google Cookies

### 🔄 В разработке

- [ ] Расширенный поиск с фильтрами
- [ ] Страницы артистов/каналов
- [ ] Радио (Mixes)
- [ ] Discord RPC
- [ ] Эквалайзер
- [ ] Автообновление

---

## 📜 Лицензия

Проект для личного использования и обучения.  
Весь аудиоконтент предоставляется YouTube.

---

> Если тебе тоже надоело, что YTM превращает твой ноут в самолёт — попробуй этот.

### Made with ❤️ for music lovers