using BepInEx.Unity.IL2CPP.Utils.Collections;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace JSMonitorPlugin;

/// <summary>
/// Drains ChatHooks.PendingEvents every 5 s and forwards them
/// to Discord (one embed per event) and to JSMonitor /api/v1/vrising/events.
/// </summary>
public class EventPushCoroutine : MonoBehaviour
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy   = new SnakeCaseNamingPolicy(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false
    };

    public void Start()
    {
        DontDestroyOnLoad(gameObject);
        StartCoroutine(EventLoop().WrapToIl2Cpp());
    }

    private IEnumerator EventLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(5f);

            // Drain queue
            var batch = new List<ServerEvent>();
            while (ChatHooks.PendingEvents.TryDequeue(out var ev))
                batch.Add(ev);

            if (batch.Count == 0) continue;

            // ── Discord ───────────────────────────────────────────────────────
            var discordUrl = Plugin.DiscordWebhookUrl.Value;
            if (!string.IsNullOrWhiteSpace(discordUrl))
            {
                foreach (var ev in batch)
                {
                    System.Threading.Tasks.Task<HttpResponseMessage>? discordTask = null;
                    try
                    {
                        var embed   = BuildDiscordEmbed(ev);
                        var payload = new DiscordWebhookPayload { Embeds = [embed] };
                        var body    = JsonSerializer.Serialize(payload, _json);
                        var content = new StringContent(body, Encoding.UTF8, "application/json");
                        discordTask = _http.PostAsync(discordUrl, content);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogError($"[JSMonitor] Discord prepare error: {ex.Message}");
                    }

                    if (discordTask != null)
                    {
                        while (!discordTask.IsCompleted)
                            yield return null;

                        try
                        {
                            if (!discordTask.Result.IsSuccessStatusCode)
                                Plugin.Logger.LogWarning(
                                    $"[JSMonitor] Discord push failed: HTTP {(int)discordTask.Result.StatusCode}");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger.LogError($"[JSMonitor] Discord send error: {ex.Message}");
                        }
                    }
                }
            }

            // ── JSMonitor /api/v1/vrising/events ──────────────────────────────
            System.Threading.Tasks.Task<HttpResponseMessage>? jsTask = null;
            int batchCount = batch.Count;

            try
            {
                var payload = new EventsPayload
                {
                    ServerId = Plugin.ServerId.Value,
                    Events   = batch.ConvertAll(e => new EventEntry
                    {
                        Type      = e.Type,
                        Player    = e.Player,
                        Channel   = string.IsNullOrEmpty(e.Channel)  ? null : e.Channel,
                        Message   = string.IsNullOrEmpty(e.Message)  ? null : e.Message,
                        Timestamp = e.Timestamp
                    })
                };

                var body    = JsonSerializer.Serialize(payload, _json);
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var url     = Plugin.JSMonitorUrl.Value.TrimEnd('/') + "/api/v1/vrising/events";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-API-Key", Plugin.ApiKey.Value);
                request.Content = content;

                jsTask = _http.SendAsync(request);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[JSMonitor] Events prepare error: {ex.Message}");
            }

            if (jsTask == null) continue;

            while (!jsTask.IsCompleted)
                yield return null;

            try
            {
                if (jsTask.Result.IsSuccessStatusCode)
                    Plugin.Logger.LogInfo($"[JSMonitor] Events pushed: {batchCount} event(s).");
                else
                    Plugin.Logger.LogWarning(
                        $"[JSMonitor] Events push failed: HTTP {(int)jsTask.Result.StatusCode}");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[JSMonitor] Events send error: {ex.Message}");
            }
        }
    }

    // ── Discord helpers ───────────────────────────────────────────────────────

    static DiscordEmbed BuildDiscordEmbed(ServerEvent ev) => ev.Type switch
    {
        "connect"    => new DiscordEmbed
        {
            Color       = 0x4CAF50,                     // green
            Description = $"✅ **{ev.Player}** подключился"
        },
        "disconnect" => new DiscordEmbed
        {
            Color       = 0xF44336,                     // red
            Description = $"❌ **{ev.Player}** отключился"
        },
        _ => BuildChatEmbed(ev)
    };

    static DiscordEmbed BuildChatEmbed(ServerEvent ev)
    {
        var (color, label) = ev.Channel switch
        {
            "clan"    => (0xFF9800, "Клан"),
            "whisper" => (0x9C27B0, "Личное"),
            "local"   => (0x9E9E9E, "Локальный"),
            _         => (0x2196F3, "Общий")            // global
        };
        return new DiscordEmbed
        {
            Color       = color,
            Description = $"**{ev.Player}** [{label}]: {ev.Message}"
        };
    }
}

// ── JSON payload models ───────────────────────────────────────────────────────

internal class EventsPayload
{
    [JsonPropertyName("server_id")]
    public int ServerId { get; set; }

    [JsonPropertyName("events")]
    public List<EventEntry> Events { get; set; } = [];
}

internal class EventEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("player")]
    public string Player { get; set; } = "";

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}

internal class DiscordWebhookPayload
{
    [JsonPropertyName("embeds")]
    public List<DiscordEmbed> Embeds { get; set; } = [];
}

internal class DiscordEmbed
{
    [JsonPropertyName("color")]
    public int Color { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}
