using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JSMonitorPlugin;

public class WarnEntry
{
    [JsonPropertyName("id")]         public string Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("steam_id")]   public string SteamId   { get; set; } = "";
    [JsonPropertyName("name")]       public string Name      { get; set; } = "";
    [JsonPropertyName("reason")]     public string Reason    { get; set; } = "";
    [JsonPropertyName("warned_by")]  public string WarnedBy  { get; set; } = "";
    [JsonPropertyName("warned_at")]  public long   WarnedAt  { get; set; }
}

public static class WarnDatabase
{
    static readonly string _path = Path.Combine(
        BepInEx.Paths.ConfigPath, "JSMonitorPlugin_warns.json");

    static readonly object _lock = new();
    static List<WarnEntry> _warns = [];

    public const int AutoBanThreshold = 3;

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _warns = JsonSerializer.Deserialize<List<WarnEntry>>(
                                 File.ReadAllText(_path)) ?? [];
                Plugin.Logger.LogInfo($"[JSMonitor] WarnDB: loaded {_warns.Count} warning(s)");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[JSMonitor] WarnDB load error: {ex.Message}");
                _warns = [];
            }
        }
    }

    static void Save()
    {
        try   { File.WriteAllText(_path, JsonSerializer.Serialize(_warns)); }
        catch (Exception ex)
        { Plugin.Logger.LogWarning($"[JSMonitor] WarnDB save error: {ex.Message}"); }
    }

    public static void AddWarn(WarnEntry entry)
    {
        lock (_lock)
        {
            _warns.Add(entry);
            Save();
        }
    }

    public static int GetWarnCount(string steamId)
    {
        lock (_lock) { return _warns.Count(w => w.SteamId == steamId); }
    }

    public static List<WarnEntry> GetWarnsForPlayer(string steamId)
    {
        lock (_lock) { return _warns.Where(w => w.SteamId == steamId).ToList(); }
    }

    public static void ClearWarns(string steamId)
    {
        lock (_lock)
        {
            int removed = _warns.RemoveAll(w => w.SteamId == steamId);
            if (removed > 0) Save();
        }
    }

    public static List<WarnEntry> GetAll()
    {
        lock (_lock) { return [.. _warns]; }
    }
}
