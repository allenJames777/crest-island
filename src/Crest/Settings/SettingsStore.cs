using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crest.Settings;

/// <summary>Runs an action once after a delay; disposing the handle cancels it.</summary>
public interface IDebounceScheduler
{
    IDisposable Schedule(TimeSpan delay, Action action);
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. A file that cannot be read is kept as
/// settings.bad.json and defaults are used, so a broken file never stops the app.
/// Changes apply at once; the file is written 300 ms after the last change.
/// </summary>
public sealed class SettingsStore
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly IDebounceScheduler _scheduler;
    private IDisposable? _pendingSave;

    public SettingsStore(string path, IDebounceScheduler scheduler)
    {
        _path = path;
        _scheduler = scheduler;
    }

    /// <summary>%APPDATA%\Crest\settings.json</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crest", "settings.json");

    public string BadFilePath => Path.Combine(Path.GetDirectoryName(_path)!, "settings.bad.json");

    public AppSettings Current { get; private set; } = AppSettings.Defaults;

    /// <summary>Raised at once after every change, with the new settings.</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        Current = ReadFile();
        return Current;
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current).Clamp();
        // Schedule the save first, so a listener that throws cannot stop the change being kept.
        _pendingSave?.Dispose();
        _pendingSave = _scheduler.Schedule(SaveDelay, () =>
        {
            _pendingSave = null;
            Save();
        });
        Changed?.Invoke(Current);
    }

    /// <summary>Writes any pending change now (used on exit).</summary>
    public void Flush()
    {
        if (_pendingSave is null) return;
        _pendingSave.Dispose();
        _pendingSave = null;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Crest: could not save settings: {ex.Message}");
        }
    }

    private AppSettings ReadFile()
    {
        string text;
        try
        {
            if (!File.Exists(_path)) return AppSettings.Defaults;
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Crest: could not read settings: {ex.Message}");
            return AppSettings.Defaults;
        }

        try
        {
            return (JsonSerializer.Deserialize<AppSettings>(text, Json) ?? AppSettings.Defaults).Clamp();
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Crest: settings file is not valid, using defaults: {ex.Message}");
            try { File.Move(_path, BadFilePath, overwrite: true); }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Crest: could not keep the bad settings file: {moveEx.Message}");
            }
            return AppSettings.Defaults;
        }
    }
}
