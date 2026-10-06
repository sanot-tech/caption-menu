// 🌐 AutoStart.cs — автозапуск с Windows через реестр (HKCU\...\Run). Самая честная и наблюдаемая штука:
//    без установщика, без Планировщика, без сервисов. Юзер видит это в «Диспетчере задач → Автозагрузка»
//    и может отключить штатным способом — ничего не прячем в обход системы.
using Microsoft.Win32; // 🗂️ Реестр: Registry.CurrentUser — ветка автозагрузок
using System.Diagnostics; // 🧪 ProcessPath — узнать путь к текущему exe

namespace CaptionMenu; // 📦 То же пространство имён

internal static class AutoStart
{
    /// <summary>📂 Имя значения в ветке Run. Обычное слово — чтобы в «Диспетчере задач» читалось понятно.</summary>
    private const string RunValueName = "CaptionMenu"; // 🏷️ Ключ автозагрузки (латиница — чтобы читалось в Task Manager у всех)

    /// <summary>🗂️ Путь к ветке автозагрузок текущего пользователя. HKCU = без прав администратора.</summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run"; // 📂 Ветка Run

    /// <summary>❓ Включён ли автозапуск СЕЙЧАС (для галочки в меню трея при старте)?</summary>
    public static bool IsEnabledForCurrentUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false); // 📖 Только чтение
            if (key?.GetValue(RunValueName) is not string cmd) return false; // 🚫 Ключа нет — автозагрузка выключена

            // 🛑 Не смогли определить путь к СВОЕМУ exe — тогда сравнивать не с чем, честно говорим «выключено».
            var exePath = GetExecutablePath(); // 📍 Наш путь
            if (string.IsNullOrEmpty(exePath)) return false; // 🚫 Неизвестно → считаем выключенным

            // 🧠 Сравниваем путь: в реестре может лежать старый путь (перенесли exe) — тогда считаем, что выключено.
            //    Так пользователь увидит «выключено» и поставит галку заново, а не будет ловить несуществующий путь.
            return cmd.Contains(exePath, StringComparison.OrdinalIgnoreCase); // ✅ Путь совпадает
        }
        catch (Exception ex)
        {
            DebugLog.Write($"autostart read failed: {ex}"); // 🧯 Реестр может быть заблокирован политикой — не роняем тулз
            return false; // 🚫 Не смогли прочитать — считаем выключенным
        }
    }

    /// <summary>💾 Включить/выключить автозапуск (для галочки в меню трея).</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true); // ✏️ Открываем на запись
            if (key == null) return; // 🛑 Не открылось — тихо выходим

            if (enabled)
            {
                // 🧠 Команда: "<путь к exe>". Кавычки ОБЯЗАТЕЛЬНЫ, если путь содержит пробелы (у нас — да: C:\Users\user\...).
                key.SetValue(RunValueName, $"\"{GetExecutablePath()}\"", RegistryValueKind.String); // 💾 Записываем
                DebugLog.Write("autostart enabled"); // 📝
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false); // 🗑️ Удаляем ключ
                DebugLog.Write("autostart disabled"); // 📝
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"autostart write failed: {ex}"); // 🧯 Политика/права — не роняем тулз
        }
    }

    /// <summary>📍 Полный путь к нашему exe. null, если не смогли определить (тогда автозапуск не пишем).</summary>
    private static string? GetExecutablePath()
    {
        try
        {
            // 🧠 Path: Environment.ProcessPath даёт путь запущенного файла (в т.ч. для single-file exe).
            //    Fallback на Process.MainModule — на старых/некоторых сборках ProcessPath может быть null.
            var path = Environment.ProcessPath; // 📍 Путь процесса
            if (!string.IsNullOrWhiteSpace(path)) return path; // ✅ Есть — возвращаем

            using var proc = Process.GetCurrentProcess(); // 🧪 Иначе — через процесс
            return proc.MainModule?.FileName; // 📍 Путь модуля (или null)
        }
        catch
        {
            return null; // 🤫 Совсем не смогли — автозапуск пропустим
        }
    }
}
