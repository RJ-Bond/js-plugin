using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JSMonitorPlugin;

public class AutoAdminEntry
{
    [JsonPropertyName("steam_id")]  public string SteamId  { get; set; } = "";
    [JsonPropertyName("name")]      public string Name     { get; set; } = "";
    [JsonPropertyName("added_by")]  public string AddedBy  { get; set; } = "";
    [JsonPropertyName("added_at")]  public long   AddedAt  { get; set; }
}

public static class AdminDatabase
{
    private static readonly string FilePath = Path.Combine(
        BepInEx.Paths.ConfigPath, "JSMonitorPlugin_autoadmins.json");

    private static List<AutoAdminEntry> _entries = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _entries = JsonSerializer.Deserialize<List<AutoAdminEntry>>(
                    File.ReadAllText(FilePath)) ?? new();
        }
        catch { _entries = new(); }
    }

    private static void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool IsAutoAdmin(string steamId) =>
        _entries.Exists(e => e.SteamId == steamId);

    public static bool Add(string steamId, string name, string addedBy)
    {
        if (IsAutoAdmin(steamId)) return false;
        _entries.Add(new AutoAdminEntry
        {
            SteamId = steamId,
            Name    = name,
            AddedBy = addedBy,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        });
        Save();
        return true;
    }

    /// <summary>Remove by SteamID or by character name (case-insensitive).</summary>
    public static AutoAdminEntry? RemoveByNameOrSteamId(string nameOrSteamId)
    {
        var entry = _entries.Find(e =>
            e.SteamId == nameOrSteamId ||
            e.Name.Equals(nameOrSteamId, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        _entries.Remove(entry);
        Save();
        return entry;
    }

    public static List<AutoAdminEntry> GetAll() => new(_entries);
}
