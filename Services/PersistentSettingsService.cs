using System.Text.Json;

namespace PresAnalysis.Services;

public sealed class PersistentSettingsService
{
    private readonly object _settingsLock = new();
    private readonly string _settingsPath;
    private HashSet<string> _disabledUserIds = new(StringComparer.Ordinal);

    public PersistentSettingsService(IWebHostEnvironment environment)
    {
        _settingsPath = Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "admin-settings.json");

        Load();
    }

    public IReadOnlySet<string> GetDisabledUserIds()
    {
        lock (_settingsLock)
            return new HashSet<string>(_disabledUserIds, StringComparer.Ordinal);
    }

    public bool IsUserVisible(string userId)
    {
        lock (_settingsLock)
            return !_disabledUserIds.Contains(userId);
    }

    public void SetDisabledUserIds(IEnumerable<string> userIds)
    {
        lock (_settingsLock)
        {
            _disabledUserIds = userIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_settingsPath)) return;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_settingsPath));
            _disabledUserIds = stored?.DisabledUserIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            _disabledUserIds = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            new StoredSettings(_disabledUserIds.OrderBy(id => id).ToList()),
            new JsonSerializerOptions { WriteIndented = true });

        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    private sealed record StoredSettings(List<string> DisabledUserIds);
}
