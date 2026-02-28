using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;
using System;
using System.Collections.Concurrent;

namespace JSMonitorPlugin;

// ── Shared event record ───────────────────────────────────────────────────────

/// <param name="Type">chat | connect | disconnect</param>
/// <param name="Player">Character name</param>
/// <param name="Channel">global | clan | whisper | local (chat only)</param>
/// <param name="Message">Chat text (chat only, empty for connect/disconnect)</param>
/// <param name="Timestamp">Unix seconds UTC</param>
public record ServerEvent(string Type, string Player, string Channel, string Message, long Timestamp);

// ── Static event queue (drained by EventPushCoroutine) ───────────────────────

public static class ChatHooks
{
    public static readonly ConcurrentQueue<ServerEvent> PendingEvents = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    static World? ServerWorld
    {
        get
        {
            if (World.s_AllWorlds == null) return null;
            foreach (var w in World.s_AllWorlds)
                if (w != null && w.Name == "Server") return w;
            return null;
        }
    }

    /// <summary>Finds the character name for a given user NetworkId.</summary>
    internal static string ResolvePlayerName(EntityManager em, NetworkId fromUser)
    {
        var qb = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        qb.AddAll(ComponentType.ReadOnly<NetworkId>());
        var query = qb.Build(em);

        string name = "Unknown";
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                try
                {
                    var netId = em.GetComponentData<NetworkId>(e);
                    if (netId.Equals(fromUser))
                    {
                        name = em.GetComponentData<User>(e).CharacterName.Value;
                        break;
                    }
                }
                catch { }
            }
            entities.Dispose();
        }
        finally
        {
            query.Dispose();
            qb.Dispose();
        }
        return name;
    }

    internal static string MapChannel(ServerChatMessageType type)
    {
        if (type == ServerChatMessageType.Global) return "global";
        if (type == ServerChatMessageType.Team)   return "clan";
        if (type == ServerChatMessageType.WhisperFrom || type == ServerChatMessageType.WhisperTo) return "whisper";
        if (type == ServerChatMessageType.Region || type == ServerChatMessageType.Local) return "local";
        return "";   // System / Lore — skip
    }

    // ── Harmony: chat ─────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(ChatMessageSystem), "OnUpdate")]
    static class ChatMessageSystemPatch
    {
        static void Postfix()
        {
            try
            {
                var world = ServerWorld;
                if (world == null) return;
                var em = world.EntityManager;

                var qb = new EntityQueryBuilder(Allocator.Temp);
                qb.AddAll(ComponentType.ReadOnly<ChatMessageServerEvent>());
                var query = qb.Build(em);

                var entities = query.ToEntityArray(Allocator.Temp);
                foreach (var entity in entities)
                {
                    try
                    {
                        var ev = em.GetComponentData<ChatMessageServerEvent>(entity);
                        var channel = MapChannel(ev.MessageType);
                        if (string.IsNullOrEmpty(channel)) continue;   // skip System/Lore

                        var playerName = ResolvePlayerName(em, ev.FromUser);
                        var ts         = ev.TimeUTC / 1000;             // ms → s

                        PendingEvents.Enqueue(new ServerEvent(
                            "chat", playerName, channel,
                            ev.MessageText.Value, ts));

                        Plugin.Logger.LogInfo(
                            $"[JSMonitor] Chat [{channel}] {playerName}: {ev.MessageText.Value}");
                    }
                    catch { }
                }
                entities.Dispose();
                query.Dispose();
                qb.Dispose();
            }
            catch { }
        }
    }

    // ── Harmony: connect / disconnect ─────────────────────────────────────────

    [HarmonyPatch(typeof(ServerBootstrapSystem), "OnUpdate")]
    static class ServerBootstrapSystemPatch
    {
        static void Postfix()
        {
            try
            {
                var world = ServerWorld;
                if (world == null) return;
                var em = world.EntityManager;

                var qb = new EntityQueryBuilder(Allocator.Temp);
                qb.AddAll(ComponentType.ReadOnly<UserConnectionChangedEvent>());
                var query = qb.Build(em);

                var entities = query.ToEntityArray(Allocator.Temp);
                foreach (var entity in entities)
                {
                    try
                    {
                        var ev = em.GetComponentData<UserConnectionChangedEvent>(entity);

                        // Skip persistence-loading events (game startup restore)
                        if (ev.IsFromPersistenceLoading) continue;

                        if (!em.Exists(ev.UserEntity)) continue;
                        var user       = em.GetComponentData<User>(ev.UserEntity);
                        var playerName = user.CharacterName.Value;
                        var isConnect  = ev.Type == UserConnectionChangedType.Connected;
                        var ts         = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                        PendingEvents.Enqueue(new ServerEvent(
                            isConnect ? "connect" : "disconnect",
                            playerName, "", "", ts));

                        Plugin.Logger.LogInfo(
                            $"[JSMonitor] {(isConnect ? "Connect" : "Disconnect")}: {playerName}");
                    }
                    catch { }
                }
                entities.Dispose();
                query.Dispose();
                qb.Dispose();
            }
            catch { }
        }
    }
}
