using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JSMonitorPlugin;

public class BanEntry
{
    [JsonPropertyName("steam_id")]   public string SteamId   { get; set; } = "";
    [JsonPropertyName("name")]       public string Name      { get; set; } = "";
    [JsonPropertyName("reason")]     public string Reason    { get; set; } = "";
    [JsonPropertyName("banned_by")]  public string BannedBy  { get; set; } = "";
    [JsonPropertyName("banned_at")]  public long   BannedAt  { get; set; }   // Unix seconds
    [JsonPropertyName("expires_at")] public long?  ExpiresAt { get; set; }   // null = permanent
}

/// <summary>
/// Thread-safe (lock-protected) persistent ban storage.
/// Stored at BepInEx/config/JSMonitorPlugin_bans.json.
/// </summary>
public static class BanDatabase
{
    static readonly string _path = Path.Combine(
        BepInEx.Paths.ConfigPath, "JSMonitorPlugin_bans.json");

    static readonly object _lock = new();
    static List<BanEntry> _bans = [];

    // ── Initialisation ────────────────────────────────────────────────────────

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _bans = JsonSerializer.Deserialize<List<BanEntry>>(
                                File.ReadAllText(_path)) ?? [];
                Plugin.Logger.LogInfo(
                    $"[JSMonitor] BanDB: loaded {_bans.Count} ban(s) from {_path}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[JSMonitor] BanDB load error: {ex.Message}");
                _bans = [];
            }
        }
    }

    // ── Write helpers ─────────────────────────────────────────────────────────

    static void Save()
    {
        try   { File.WriteAllText(_path, JsonSerializer.Serialize(_bans)); }
        catch (Exception ex)
        { Plugin.Logger.LogWarning($"[JSMonitor] BanDB save error: {ex.Message}"); }
    }

    public static void AddBan(BanEntry entry)
    {
        lock (_lock)
        {
            _bans.RemoveAll(b => b.SteamId == entry.SteamId);
            _bans.Add(entry);
            Save();
        }
        var expiry = entry.ExpiresAt.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds(entry.ExpiresAt.Value).ToString("u")
            : "never";
        Plugin.Logger.LogInfo(
            $"[JSMonitor] Banned {entry.Name} ({entry.SteamId}) until {expiry}. Reason: {entry.Reason}");
    }

    public static void Unban(string steamId)
    {
        lock (_lock)
        {
            int removed = _bans.RemoveAll(b => b.SteamId == steamId);
            if (removed > 0) Save();
        }
        Plugin.Logger.LogInfo($"[JSMonitor] Unbanned SteamID {steamId}");
    }

    // ── Read helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the active ban for this Steam ID, or null if not banned / ban expired.
    /// Automatically removes expired bans.
    /// </summary>
    public static BanEntry? GetActiveBan(string steamId)
    {
        lock (_lock)
        {
            var ban = _bans.FirstOrDefault(b => b.SteamId == steamId);
            if (ban == null) return null;

            if (ban.ExpiresAt.HasValue &&
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ban.ExpiresAt.Value)
            {
                _bans.Remove(ban);
                Save();
                Plugin.Logger.LogInfo(
                    $"[JSMonitor] Ban expired for {ban.Name} ({steamId}), auto-removed.");
                return null;
            }
            return ban;
        }
    }

    /// <summary>Returns a snapshot of all active bans (expired ones pruned first).</summary>
    public static List<BanEntry> GetAll()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _bans.RemoveAll(b => b.ExpiresAt.HasValue && now >= b.ExpiresAt.Value);
            return [.. _bans];
        }
    }

    // ── Duration parser ───────────────────────────────────────────────────────

    /// <summary>
    /// Parses duration string: "1h", "2d", "30m", "0" (permanent).
    /// Returns null for permanent, or a future Unix timestamp.
    /// </summary>
    public static long? ParseDuration(string raw)
    {
        raw = raw.Trim().ToLowerInvariant();
        if (raw == "0" || raw == "perm" || raw == "permanent") return null;

        if (raw.EndsWith("m") && int.TryParse(raw[..^1], out int mins))
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + mins * 60L;
        if (raw.EndsWith("h") && int.TryParse(raw[..^1], out int hours))
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + hours * 3600L;
        if (raw.EndsWith("d") && int.TryParse(raw[..^1], out int days))
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + days * 86400L;

        return null; // unrecognised → treat as permanent
    }
}
