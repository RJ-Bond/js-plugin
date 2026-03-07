using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JSMonitorPlugin;

public class ModlogEntry
{
    [JsonPropertyName("id")]         public string Id        { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("action")]     public string Action    { get; set; } = ""; // ban|kick|mute|warn|unban|unmute|clearwarns
    [JsonPropertyName("steam_id")]   public string SteamId   { get; set; } = "";
    [JsonPropertyName("name")]       public string Name      { get; set; } = "";
    [JsonPropertyName("reason")]     public string Reason    { get; set; } = "";
    [JsonPropertyName("by")]         public string By        { get; set; } = "";
    [JsonPropertyName("at")]         public long   At        { get; set; }
    [JsonPropertyName("expires_at")] public long?  ExpiresAt { get; set; }
}

/// <summary>
/// Persistent moderation history — last 1000 entries per server.
/// Stored at BepInEx/config/JSMonitorPlugin_modlog.json.
/// </summary>
public static class ModlogDatabase
{
    static readonly string _path = Path.Combine(
        BepInEx.Paths.ConfigPath, "JSMonitorPlugin_modlog.json");

    static readonly object _lock = new();
    static List<ModlogEntry> _entries = [];
    const int MaxEntries = 1000;

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _entries = JsonSerializer.Deserialize<List<ModlogEntry>>(
                                   File.ReadAllText(_path)) ?? [];
                Plugin.Logger.LogInfo($"[JSMonitor] ModlogDB: loaded {_entries.Count} entry(ies)");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[JSMonitor] ModlogDB load error: {ex.Message}");
                _entries = [];
            }
        }
    }

    static void Save()
    {
        try   { File.WriteAllText(_path, JsonSerializer.Serialize(_entries)); }
        catch (Exception ex) { Plugin.Logger.LogWarning($"[JSMonitor] ModlogDB save error: {ex.Message}"); }
    }

    public static void Log(ModlogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            Save();
        }
    }

    /// <summary>Returns the most recent entries for a player by SteamID.</summary>
    public static List<ModlogEntry> GetForPlayer(string steamId, int limit = 15)
    {
        lock (_lock)
        {
            return _entries
                .Where(e => e.SteamId == steamId)
                .OrderByDescending(e => e.At)
                .Take(limit)
                .ToList();
        }
    }
}
