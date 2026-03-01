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
                    FreePlots = snapshot.FreePlots
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

            // Phase 3: log result
            try
            {
                if (sendTask.Result.IsSuccessStatusCode)
                    Plugin.Logger.LogInfo($"[JSMonitor] Pushed {playerCount} players, {castleCount} castles, {freePlotCount} free plots.");
                else
                    Plugin.Logger.LogWarning($"[JSMonitor] Push failed: HTTP {(int)sendTask.Result.StatusCode}");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"[JSMonitor] Push error: {ex.Message}");
            }
        }
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
