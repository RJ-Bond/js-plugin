using BepInEx.Unity.IL2CPP.Utils.Collections;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unity.Entities;
using UnityEngine;

namespace JSMonitorPlugin;

/// <summary>
/// MonoBehaviour component that runs the push loop as an IL2CPP coroutine.
/// Attached to a persistent GameObject by Plugin.Load().
/// </summary>
public class MapPushCoroutine : MonoBehaviour
{
    // Bypass cert validation so the plugin can talk to self-signed / Let's Encrypt certs alike.
    private static readonly HttpClient _http = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        })
    { Timeout = System.TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = new SnakeCaseNamingPolicy(),
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = false
    };

    public void Start()
    {
        DontDestroyOnLoad(gameObject);
        StartCoroutine(PushLoop().WrapToIl2Cpp());
    }

    private IEnumerator PushLoop()
    {
        int interval = Math.Max(10, Plugin.UpdateInterval.Value);

        while (true)
        {
            yield return new WaitForSeconds(interval);

            // ── Component dump trigger ────────────────────────────────────
            try
            {
                Plugin.Instance.Config.Reload();
                if (Plugin.DumpOnNextPush.Value)
                {
                    Plugin.DumpOnNextPush.Value = false;
                    Plugin.Instance.Config.Save();
                    MapDataCollector.DumpTerritoryComponents();
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[JSMonitor] Dump-cfg error: {ex.Message}"); }

            // Phase 1: collect data and start HTTP request
            System.Threading.Tasks.Task<HttpResponseMessage>? sendTask = null;
            int playerCount = 0, castleCount = 0, freePlotCount = 0;

            try
            {
                var snapshot = MapDataCollector.Collect();
                if (snapshot == null)
                {
                    Plugin.Logger.LogInfo("[JSMonitor] Server world not ready yet — skipping push.");
                    continue;
                }

                var payload = new PushPayload
                {
                    ServerId  = Plugin.ServerId.Value,
                    Players   = snapshot.Players,
                    Castles   = snapshot.Castles,
                    FreePlots = snapshot.FreePlots,
                    Bans      = BanDatabase.GetAll().ConvertAll(b => new BanPayloadEntry
                    {
                        SteamId   = b.SteamId,
                        Name      = b.Name,
                        Reason    = b.Reason,
                        BannedBy  = b.BannedBy,
                        BannedAt  = b.BannedAt,
                        ExpiresAt = b.ExpiresAt,
                    }),
                    Mutes     = MuteDatabase.GetAll().ConvertAll(m => new MutePayloadEntry
                    {
                        SteamId   = m.SteamId,
                        Name      = m.Name,
                        Reason    = m.Reason,
                        MutedBy   = m.MutedBy,
                        MutedAt   = m.MutedAt,
                        ExpiresAt = m.ExpiresAt,
                    }),
                    Warns     = WarnDatabase.GetAll().ConvertAll(w => new WarnPayloadEntry
                    {
                        Id        = w.Id,
                        SteamId   = w.SteamId,
                        Name      = w.Name,
                        Reason    = w.Reason,
                        WarnedBy  = w.WarnedBy,
                        WarnedAt  = w.WarnedAt,
                    }),
                };

                var json    = JsonSerializer.Serialize(payload, _json);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url     = Plugin.JSMonitorUrl.Value.TrimEnd('/') + "/api/v1/vrising/push";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-API-Key", Plugin.ApiKey.Value);
                request.Content = content;

                sendTask      = _http.SendAsync(request);
                playerCount   = snapshot.Players.Count;
                castleCount   = snapshot.Castles.Count;
                freePlotCount = snapshot.FreePlots.Count;
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"[JSMonitor] Collect error: {ex.Message}");
            }

            if (sendTask == null) continue;

            // Phase 2: yield each frame while waiting (outside try/catch — C# requirement)
            while (!sendTask.IsCompleted)
                yield return null;

            // Phase 3: parse response and execute pending commands
            try
            {
                var resp = sendTask.Result;
                if (resp.IsSuccessStatusCode)
                {
                    Plugin.Logger.LogInfo(
                        $"[JSMonitor] Pushed {playerCount} players, {castleCount} castles, {freePlotCount} free plots.");

                    // Read pending mod commands from response body
                    var body = resp.Content.ReadAsStringAsync().Result;
                    ExecuteIncomingCommands(body);
                }
                else
                {
                    Plugin.Logger.LogWarning($"[JSMonitor] Push failed: HTTP {(int)resp.StatusCode}");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"[JSMonitor] Push error: {ex.Message}");
            }
        }
    }

    // ── Incoming command executor ─────────────────────────────────────────────

    void ExecuteIncomingCommands(string responseBody)
    {
        try
        {
            var resp = JsonSerializer.Deserialize<PushResponse>(responseBody, _json);

            // Update announcements from backend
            if (resp?.Announcements != null)
            {
                var list = new System.Collections.Generic.List<RemoteAnnouncement>();
                foreach (var a in resp.Announcements)
                    list.Add(new RemoteAnnouncement
                    {
                        Id              = a.Id,
                        Message         = a.Message,
                        IntervalSeconds = a.IntervalSeconds,
                    });
                AutoAnnouncer.UpdateAnnouncements(list, resp.AnnouncementsRandom);
            }

            if (resp?.Commands == null || resp.Commands.Count == 0) return;

            World? serverWorld = null;
            if (World.s_AllWorlds != null)
                foreach (var w in World.s_AllWorlds)
                    if (w?.Name == "Server") { serverWorld = w; break; }
            if (serverWorld == null) return;

            var em = serverWorld.EntityManager;

            foreach (var cmd in resp.Commands)
            {
                try
                {
                    Plugin.Logger.LogInfo(
                        $"[JSMonitor] Remote command: {cmd.Type} player='{cmd.PlayerName}' reason='{cmd.Reason}'");

                    switch (cmd.Type?.ToLowerInvariant())
                    {
                        case "kick":
                            ExecuteRemoteKick(cmd, em);
                            break;
                        case "ban":
                            ExecuteRemoteBan(cmd, em);
                            break;
                        case "unban":
                            if (!string.IsNullOrEmpty(cmd.SteamId))
                                BanDatabase.Unban(cmd.SteamId);
                            break;
                        case "announce":
                            if (!string.IsNullOrEmpty(cmd.Reason))
                                ModerationHelpers.BroadcastMessage(em,
                                    $"{cmd.Reason}\n" +
                                    $"<color=#ff5555>━━━━━━━━━━━━━━━━━━━━━━━━</color>");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[JSMonitor] Command exec error ({cmd.Type}): {ex.Message}");
                }
            }
        }
        catch { /* ignore malformed responses */ }
    }

    static void ExecuteRemoteKick(ModCommand cmd, EntityManager em)
    {
        if (string.IsNullOrEmpty(cmd.PlayerName)) return;
        if (!ModerationHelpers.TryFindUser(em, cmd.PlayerName, out var ue, out var user)) return;

        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{user.CharacterName.Value}</color> был кикнут админом <color=#00ccff>Web Panel</color>. Причина: <color=#ff8800>{cmd.Reason ?? ""}</color>");
        ModerationHelpers.KickUser(ue, em, cmd.Reason ?? "kicked via web panel");
    }

    static void ExecuteRemoteBan(ModCommand cmd, EntityManager em)
    {
        if (string.IsNullOrEmpty(cmd.PlayerName)) return;

        long? expires = cmd.DurationSeconds > 0
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + cmd.DurationSeconds
            : (long?)null;

        // If player is online, get their SteamID; else use the one from command
        string steamId = cmd.SteamId ?? "";
        string charName = cmd.PlayerName;

        if (ModerationHelpers.TryFindUser(em, cmd.PlayerName, out var ue, out var user))
        {
            steamId  = user.PlatformId.ToString();
            charName = user.CharacterName.Value;
            var banReason = cmd.Reason ?? "banned via web panel";
            var banDur = cmd.DurationSeconds > 0
                ? TimeSpan.FromSeconds(cmd.DurationSeconds).ToString(@"d\d\ h\h")
                : "навсегда";
            ModerationHelpers.KickUser(ue, em, $"Бан на {banDur}. Причина: {banReason}", expires ?? 0L);
        }

        if (string.IsNullOrEmpty(steamId)) return;

        BanDatabase.AddBan(new BanEntry
        {
            SteamId   = steamId,
            Name      = charName,
            Reason    = cmd.Reason ?? "banned via web panel",
            BannedBy  = "web panel",
            BannedAt  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = expires,
        });

        var durDisplay = expires.HasValue
            ? TimeSpan.FromSeconds(expires.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(@"d\d\ h\h")
            : "permanent";
        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{charName}</color> забанен админом <color=#00ccff>Web Panel</color> на <color=#ffffff>{durDisplay}</color>. Причина: <color=#ff8800>{cmd.Reason ?? ""}</color>");
    }
}

// ── Push payload (matches backend VRisingMapPayload) ─────────────────────────

public class PushPayload
{
    [JsonPropertyName("server_id")]
    public int ServerId { get; set; }

    [JsonPropertyName("players")]
    public List<PlayerEntry> Players { get; set; } = [];

    [JsonPropertyName("castles")]
    public List<CastleEntry> Castles { get; set; } = [];

    [JsonPropertyName("free_plots")]
    public List<FreePlotEntry> FreePlots { get; set; } = [];

    [JsonPropertyName("bans")]
    public List<BanPayloadEntry> Bans { get; set; } = [];

    [JsonPropertyName("mutes")]
    public List<MutePayloadEntry> Mutes { get; set; } = [];

    [JsonPropertyName("warns")]
    public List<WarnPayloadEntry> Warns { get; set; } = [];
}

public class BanPayloadEntry
{
    [JsonPropertyName("steam_id")]   public string SteamId   { get; set; } = "";
    [JsonPropertyName("name")]       public string Name      { get; set; } = "";
    [JsonPropertyName("reason")]     public string Reason    { get; set; } = "";
    [JsonPropertyName("banned_by")]  public string BannedBy  { get; set; } = "";
    [JsonPropertyName("banned_at")]  public long   BannedAt  { get; set; }
    [JsonPropertyName("expires_at")] public long?  ExpiresAt { get; set; }
}

public class MutePayloadEntry
{
    [JsonPropertyName("steam_id")]   public string SteamId   { get; set; } = "";
    [JsonPropertyName("name")]       public string Name      { get; set; } = "";
    [JsonPropertyName("reason")]     public string Reason    { get; set; } = "";
    [JsonPropertyName("muted_by")]   public string MutedBy   { get; set; } = "";
    [JsonPropertyName("muted_at")]   public long   MutedAt   { get; set; }
    [JsonPropertyName("expires_at")] public long?  ExpiresAt { get; set; }
}

public class WarnPayloadEntry
{
    [JsonPropertyName("id")]         public string Id        { get; set; } = "";
    [JsonPropertyName("steam_id")]   public string SteamId   { get; set; } = "";
    [JsonPropertyName("name")]       public string Name      { get; set; } = "";
    [JsonPropertyName("reason")]     public string Reason    { get; set; } = "";
    [JsonPropertyName("warned_by")]  public string WarnedBy  { get; set; } = "";
    [JsonPropertyName("warned_at")]  public long   WarnedAt  { get; set; }
}

// ── Push response (from backend) ──────────────────────────────────────────────

public class PushResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("commands")]
    public List<ModCommand>? Commands { get; set; }

    [JsonPropertyName("announcements")]
    public List<AnnouncementPayload>? Announcements { get; set; }

    [JsonPropertyName("announcements_random")]
    public bool AnnouncementsRandom { get; set; }
}

public class AnnouncementPayload
{
    [JsonPropertyName("id")]               public uint   Id              { get; set; }
    [JsonPropertyName("message")]          public string Message         { get; set; } = "";
    [JsonPropertyName("interval_seconds")] public int    IntervalSeconds { get; set; }
}

public class ModCommand
{
    [JsonPropertyName("id")]              public long   Id              { get; set; }
    [JsonPropertyName("type")]            public string Type            { get; set; } = "";
    [JsonPropertyName("player_name")]     public string? PlayerName     { get; set; }
    [JsonPropertyName("steam_id")]        public string? SteamId        { get; set; }
    [JsonPropertyName("reason")]          public string? Reason         { get; set; }
    [JsonPropertyName("duration_seconds")] public long  DurationSeconds { get; set; }
}

// ── snake_case naming policy for net6 (SnakeCaseLower added in net8) ─────────

internal sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                sb.Append('_');
            sb.Append(char.ToLower(name[i]));
        }
        return sb.ToString();
    }
}
