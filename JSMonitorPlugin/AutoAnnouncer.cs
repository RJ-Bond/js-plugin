using BepInEx.Unity.IL2CPP.Utils.Collections;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Unity.Entities;
using UnityEngine;

namespace JSMonitorPlugin;

/// <summary>
/// Each announcement has its own interval.
/// The list is populated from the web panel via push response.
/// </summary>
public class RemoteAnnouncement
{
    public uint   Id              { get; set; }
    public string Message         { get; set; } = "";
    public int    IntervalSeconds { get; set; }
    public long   LastSentAt      { get; set; } // Unix seconds
}

/// <summary>
/// Runs each remote announcement on its own independent interval.
/// Updated by MapPushCoroutine each push cycle.
/// </summary>
public class AutoAnnouncer : MonoBehaviour
{
    // Thread-safe list updated from push coroutine
    static readonly object _lock = new();
    static List<RemoteAnnouncement> _announcements = [];
    static bool _randomOrder = false;
    static readonly Random _rng = new();

    public void Start()
    {
        DontDestroyOnLoad(gameObject);
        StartCoroutine(AnnounceLoop().WrapToIl2Cpp());
    }

    /// <summary>Called by MapPushCoroutine after each successful push.</summary>
    public static void UpdateAnnouncements(List<RemoteAnnouncement> incoming, bool randomOrder = false)
    {
        lock (_lock)
        {
            var now = new List<RemoteAnnouncement>();
            foreach (var inc in incoming)
            {
                // Preserve LastSentAt for existing entries so timing isn't reset
                var existing = _announcements.Find(a => a.Id == inc.Id);
                inc.LastSentAt = existing?.LastSentAt ?? 0;
                now.Add(inc);
            }
            _announcements = now;
            _randomOrder   = randomOrder;
        }
        Plugin.Logger.LogInfo($"[JSMonitor] AutoAnnouncer: {incoming.Count} announcement(s) loaded from server. RandomOrder={randomOrder}");
    }

    static void SendAnnouncement(Unity.Entities.EntityManager em, RemoteAnnouncement ann)
    {
        ModerationHelpers.BroadcastMessage(em,
            $"<color=#55ff55>[!]</color> {ann.Message}\n" +
            $"<color=#ff5555>━━━━━━━━━━━━━━━━━━━━━━━━</color>");
        lock (_lock)
        {
            var a = _announcements.Find(x => x.Id == ann.Id);
            if (a != null) a.LastSentAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    IEnumerator AnnounceLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(5); // check every 5 seconds

            List<RemoteAnnouncement> candidates;
            bool random;
            var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (_lock)
            {
                candidates = _announcements.FindAll(a =>
                    a.IntervalSeconds > 0 &&
                    nowSec - a.LastSentAt >= a.IntervalSeconds);
                random = _randomOrder;
            }

            if (candidates.Count == 0) continue;

            World? serverWorld = null;
            if (World.s_AllWorlds != null)
                foreach (var w in World.s_AllWorlds)
                    if (w?.Name == "Server") { serverWorld = w; break; }
            if (serverWorld == null) continue;

            var em = serverWorld.EntityManager;

            if (random)
            {
                // Random mode: pick one announcement at random from those that are due
                try
                {
                    var pick = candidates[_rng.Next(candidates.Count)];
                    SendAnnouncement(em, pick);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning($"[JSMonitor] AutoAnnouncer send error: {ex.Message}");
                }
            }
            else
            {
                // Normal mode: send all due announcements in order
                foreach (var ann in candidates)
                {
                    try { SendAnnouncement(em, ann); }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogWarning($"[JSMonitor] AutoAnnouncer send error: {ex.Message}");
                    }
                }
            }
        }
    }
}
