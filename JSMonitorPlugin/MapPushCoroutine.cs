using BepInEx.Unity.IL2CPP.Utils.Collections;
using System.Collections;
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
    private static readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromSeconds(10) };

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingDefault,
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

            try
            {
                var snapshot = MapDataCollector.Collect();
                if (snapshot == null)
                {
                    Plugin.Logger.LogDebug("[JSMonitor] Server world not ready yet — skipping push.");
                    continue;
                }

                var payload = new PushPayload
                {
                    ServerId = Plugin.ServerId.Value,
                    Players  = snapshot.Players,
                    Castles  = snapshot.Castles
                };

                var json    = JsonSerializer.Serialize(payload, _json);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var url     = Plugin.JSMonitorUrl.Value.TrimEnd('/') + "/api/v1/vrising/push";
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Add("X-API-Key", Plugin.ApiKey.Value);
                request.Content = content;

                var task   = _http.SendAsync(request);
                task.Wait(); // blocking is fine in coroutine on background thread logic

                if (task.Result.IsSuccessStatusCode)
                    Plugin.Logger.LogDebug($"[JSMonitor] Pushed {snapshot.Players.Count} players, {snapshot.Castles.Count} castles.");
                else
                    Plugin.Logger.LogWarning($"[JSMonitor] Push failed: HTTP {(int)task.Result.StatusCode}");
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
}
