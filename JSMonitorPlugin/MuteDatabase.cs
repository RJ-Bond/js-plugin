using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JSMonitorPlugin;

public class MuteEntry
{
    [JsonPropertyName("steam_id")]  public string SteamId  { get; set; } = "";
    [JsonPropertyName("name")]      public string Name     { get; set; } = "";
    [JsonPropertyName("reason")]    public string Reason   { get; set; } = "";
    [JsonPropertyName("muted_by")]  public string MutedBy  { get; set; } = "";
    [JsonPropertyName("muted_at")]  public long   MutedAt  { get; set; }
    [JsonPropertyName("expires_at")] public long? ExpiresAt { get; set; } // null = permanent
}

public static class MuteDatabase
{
    static readonly string _path = Path.Combine(
        BepInEx.Paths.ConfigPath, "JSMonitorPlugin_mutes.json");

    static readonly object _lock = new();
    static List<MuteEntry> _mutes = [];

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _mutes = JsonSerializer.Deserialize<List<MuteEntry>>(
                                 File.ReadAllText(_path)) ?? [];
                Plugin.Logger.LogInfo($"[JSMonitor] MuteDB: loaded {_mutes.Count} mute(s)");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[JSMonitor] MuteDB load error: {ex.Message}");
                _mutes = [];
            }
        }
    }

    static void Save()
    {
        try   { File.WriteAllText(_path, JsonSerializer.Serialize(_mutes)); }
        catch (Exception ex)
        { Plugin.Logger.LogWarning($"[JSMonitor] MuteDB save error: {ex.Message}"); }
    }

    public static void AddMute(MuteEntry entry)
    {
        lock (_lock)
        {
            _mutes.RemoveAll(m => m.SteamId == entry.SteamId);
            _mutes.Add(entry);
            Save();
        }
    }

    public static void Unmute(string steamId)
    {
        lock (_lock)
        {
            int removed = _mutes.RemoveAll(m => m.SteamId == steamId);
            if (removed > 0) Save();
        }
    }

    public static MuteEntry? GetActiveMute(string steamId)
    {
        lock (_lock)
        {
            var mute = _mutes.FirstOrDefault(m => m.SteamId == steamId);
            if (mute == null) return null;

            if (mute.ExpiresAt.HasValue &&
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= mute.ExpiresAt.Value)
            {
                _mutes.Remove(mute);
                Save();
                return null;
            }
            return mute;
        }
    }

    public static List<MuteEntry> GetAll()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _mutes.RemoveAll(m => m.ExpiresAt.HasValue && now >= m.ExpiresAt.Value);
            return [.. _mutes];
        }
    }
}
