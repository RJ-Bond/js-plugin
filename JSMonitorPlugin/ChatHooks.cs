using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
        // Prefix runs BEFORE ChatMessageSystem processes events.
        // We remove muted/filtered messages here so the system never sees them.
        static void Prefix()
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
                        var ev      = em.GetComponentData<ChatMessageServerEvent>(entity);
                        var msgText = ev.MessageText.Value ?? "";

                        var channel = MapChannel(ev.MessageType);
                        if (string.IsNullOrEmpty(channel)) continue;

                        // ── Suppress command messages from public chat ────────
                        // VCF queues the command in its own Prefix but does not
                        // destroy the entity, so ChatMessageSystem would broadcast
                        // the raw ".command text" to all players. We destroy it here.
                        if (msgText.StartsWith("."))
                        {
                            em.DestroyEntity(entity);
                            continue;
                        }

                        var steamId = ResolvePlayerSteamId(em, ev.FromUser);
                        if (steamId == null) continue;

                        // ── Mute check ───────────────────────────────────────
                        var mute = MuteDatabase.GetActiveMute(steamId);
                        if (mute != null)
                        {
                            var userEntity = ResolveUserEntity(em, ev.FromUser);
                            if (userEntity != Entity.Null)
                            {
                                var expiry = mute.ExpiresAt.HasValue
                                    ? DateTimeOffset.FromUnixTimeSeconds(mute.ExpiresAt.Value).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm")
                                    : "навсегда";
                                ModerationHelpers.SendMessageToUser(em, userEntity,
                                    $"<color=red>* Вы замьючены до {expiry}. Причина: {mute.Reason}</color>");
                            }
                            em.DestroyEntity(entity);
                            continue;
                        }

                        // ── Chat filter check ────────────────────────────────
                        var badWord = ChatFilter.CheckMessage(msgText);
                        if (badWord != null)
                        {
                            var userEntity = ResolveUserEntity(em, ev.FromUser);
                            if (userEntity != Entity.Null)
                            {
                                ModerationHelpers.SendMessageToUser(em, userEntity,
                                    $"<color=red>* Ваше сообщение содержит запрещённое слово и было заблокировано.</color>");
                            }
                            em.DestroyEntity(entity);

                            var playerName = ResolvePlayerName(em, ev.FromUser);
                            PendingEvents.Enqueue(new ServerEvent(
                                "moderation", "system", "filter",
                                $"Blocked message from {playerName}: \"{msgText}\" (matched: {badWord})",
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                        }
                    }
                    catch { }
                }
                entities.Dispose();
                query.Dispose();
                qb.Dispose();
            }
            catch { }
        }

        // Postfix only logs and forwards surviving chat events to the backend.
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
                        var ev      = em.GetComponentData<ChatMessageServerEvent>(entity);
                        var msgText = ev.MessageText.Value ?? "";
                        var channel = MapChannel(ev.MessageType);
                        if (string.IsNullOrEmpty(channel)) continue;

                        var playerName = ResolvePlayerName(em, ev.FromUser);
                        var ts         = ev.TimeUTC / 1000;

                        PendingEvents.Enqueue(new ServerEvent("chat", playerName, channel, msgText, ts));
                        Plugin.Logger.LogInfo($"[JSMonitor] Chat [{channel}] {playerName}: {msgText}");
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

    /// <summary>Resolves SteamID for a given NetworkId.</summary>
    internal static string? ResolvePlayerSteamId(EntityManager em, NetworkId fromUser)
    {
        var qb = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        qb.AddAll(ComponentType.ReadOnly<NetworkId>());
        var query = qb.Build(em);
        string? steamId = null;
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
                        steamId = em.GetComponentData<User>(e).PlatformId.ToString();
                        break;
                    }
                }
                catch { }
            }
            entities.Dispose();
        }
        finally { query.Dispose(); qb.Dispose(); }
        return steamId;
    }

    /// <summary>Resolves the User entity for a given NetworkId.</summary>
    internal static Entity ResolveUserEntity(EntityManager em, NetworkId fromUser)
    {
        var qb = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        qb.AddAll(ComponentType.ReadOnly<NetworkId>());
        var query = qb.Build(em);
        Entity result = Entity.Null;
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                try
                {
                    var netId = em.GetComponentData<NetworkId>(e);
                    if (netId.Equals(fromUser)) { result = e; break; }
                }
                catch { }
            }
            entities.Dispose();
        }
        finally { query.Dispose(); qb.Dispose(); }
        return result;
    }

    // ── Harmony: connect / disconnect + admin auth detection ─────────────────

    [HarmonyPatch(typeof(ServerBootstrapSystem), "OnUpdate")]
    static class ServerBootstrapSystemPatch
    {
        // Tracks last known IsAdmin state per PlatformId for connected players.
        // Used to detect the moment a player gains admin rights (.adminauth).
        static readonly Dictionary<ulong, bool> _adminState = new();

        static void Postfix()
        {
            try
            {
                var world = ServerWorld;
                if (world == null) return;
                var em = world.EntityManager;

                // ── Connection events ─────────────────────────────────────────
                var qb = new EntityQueryBuilder(Allocator.Temp);
                qb.AddAll(ComponentType.ReadOnly<UserConnectionChangedEvent>());
                var query = qb.Build(em);

                var entities = query.ToEntityArray(Allocator.Temp);
                foreach (var entity in entities)
                {
                    try
                    {
                        var ev = em.GetComponentData<UserConnectionChangedEvent>(entity);

                        if (ev.IsFromPersistenceLoading) continue;
                        if (!em.Exists(ev.UserEntity)) continue;

                        var user       = em.GetComponentData<User>(ev.UserEntity);
                        var playerName = user.CharacterName.Value;
                        if (string.IsNullOrEmpty(playerName)) continue;

                        var isConnect = ev.Type == UserConnectionChangedType.Connected;
                        var ts        = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                        if (isConnect && ModerationHelpers.CheckBanOnConnect(ev.UserEntity, em))
                            continue;

                        // Auto-admin: grant rights if SteamID is in the trusted list
                        if (isConnect && !user.IsAdmin)
                        {
                            var sid = user.PlatformId.ToString();
                            if (AdminDatabase.IsAutoAdmin(sid))
                            {
                                ModerationHelpers.GrantAdminRights(em, ev.UserEntity, user.PlatformId);
                                Plugin.Logger.LogInfo($"[JSMonitor] Auto-admin granted to {playerName} ({sid})");
                                // _adminState will be seeded as false below, so the welcome message
                                // will fire naturally when IsAdmin flips in the next detection loop.
                            }
                        }

                        PendingEvents.Enqueue(new ServerEvent(
                            isConnect ? "connect" : "disconnect",
                            playerName, "", "", ts));

                        Plugin.Logger.LogInfo(
                            $"[JSMonitor] {(isConnect ? "Connect" : "Disconnect")}: {playerName}");

                        if (isConnect)
                        {
                            ModerationHelpers.BroadcastMessage(em,
                                $"<color=#44ff44>* Игрок</color> <color=#88ccff>{playerName}</color> <color=#44ff44>подключился к серверу.</color>");
                            // Seed admin state so a connect as admin doesn't trigger welcome
                            _adminState[user.PlatformId] = user.IsAdmin;
                        }
                        else
                        {
                            ModerationHelpers.BroadcastMessage(em,
                                $"<color=#ff8844>* Игрок</color> <color=#88ccff>{playerName}</color> <color=#ff8844>отключился от сервера.</color>");
                            _adminState.Remove(user.PlatformId);
                        }
                    }
                    catch { }
                }
                entities.Dispose();
                query.Dispose();
                qb.Dispose();

                // ── Admin auth detection ──────────────────────────────────────
                // Check all connected users; if IsAdmin flipped true → send welcome.
                var qb2 = new EntityQueryBuilder(Allocator.Temp);
                qb2.AddAll(ComponentType.ReadOnly<User>());
                var q2 = qb2.Build(em);
                var userEntities = q2.ToEntityArray(Allocator.Temp);
                foreach (var ue in userEntities)
                {
                    try
                    {
                        var u = em.GetComponentData<User>(ue);
                        if (!u.IsConnected) continue;

                        var id = u.PlatformId;
                        bool wasAdmin = _adminState.TryGetValue(id, out var prev) && prev;
                        _adminState[id] = u.IsAdmin;

                        if (u.IsAdmin && !wasAdmin)
                        {
                            var name = u.CharacterName.Value;
                            if (string.IsNullOrEmpty(name)) continue;

                            ModerationHelpers.SendMessageToUser(em, ue,
                                $"<color=#ffcc00>* Привет! {name}</color>");
                            ModerationHelpers.SendMessageToUser(em, ue,
                                "<color=#00ccff>* Вам как админу доступны команды:</color>");
                            ModerationHelpers.SendMessageToUser(em, ue,
                                "<color=#ffffff>* .js-help</color>");

                            Plugin.Logger.LogInfo($"[JSMonitor] Admin auth detected: {name}");

                            // Auto-add to AdminDatabase on first successful adminauth
                            var sid = u.PlatformId.ToString();
                            if (!AdminDatabase.IsAutoAdmin(sid))
                            {
                                AdminDatabase.Add(sid, name, "auto");
                                ModerationHelpers.SendMessageToUser(em, ue,
                                    "<color=#44ff44>* Вы добавлены в список авто-авторизации. При следующем входе права будут выданы автоматически.</color>");
                                Plugin.Logger.LogInfo($"[JSMonitor] Auto-admin added on first auth: {name} ({sid})");
                            }
                        }
                    }
                    catch { }
                }
                userEntities.Dispose();
                q2.Dispose();
                qb2.Dispose();
            }
            catch { }
        }
    }
}
