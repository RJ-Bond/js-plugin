using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using VampireCommandFramework;

namespace JSMonitorPlugin;

/// <summary>
/// VCF chat commands for moderation:
///   .kick  PlayerName [Reason]
///   .ban   PlayerName Duration [Reason]   (duration: 1h / 2d / 0 = permanent)
///   .unban SteamID
///
/// Ban-on-login check is called from ChatHooks.ServerBootstrapSystemPatch.
/// Remote commands from the web panel are executed by MapPushCoroutine.
/// </summary>
public class ModerationVCFCommands
{
    // ── .kick PlayerName [Reason] ────────────────────────────────────────────
    [Command("kick", description: "Kick a player")]
    public void Kick(ChatCommandContext ctx, string playerName = "", string reason = "kicked by admin")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(playerName))
        {
            ctx.Reply("Использование: .kick <игрок> [причина]");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        if (!ModerationHelpers.TryFindUser(em, playerName, out var ue, out var user))
        {
            ctx.Reply($"Игрок '{playerName}' не найден онлайн.");
            return;
        }

        var steamId  = user.PlatformId.ToString();
        var charName = user.CharacterName.Value;

        ModerationHelpers.BroadcastMessage(em, $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{charName}</color> был кикнут админом <color=#00ccff>{by}</color>. Причина: <color=#ff8800>{reason}</color>");
        ModerationHelpers.KickUser(ue, em, reason);
        ctx.Reply($"Kicked {charName} ({steamId})");

        ChatHooks.PendingEvents.Enqueue(new ServerEvent(
            "moderation", by, "kick",
            $"Kicked {charName} ({steamId}): {reason}",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .ban PlayerName/SteamID Duration [Reason] ────────────────────────────
    [Command("ban", description: "Ban a player (online or offline, by name or SteamID)")]
    public void Ban(ChatCommandContext ctx, string playerName = "", string duration = "", string reason = "banned by admin")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(duration))
        {
            ctx.Reply("Использование: .ban <игрок|SteamID> <длительность> [причина]  (пример: .ban Vasya 1d читерство)");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        var expiresAt = BanDatabase.ParseDuration(duration);

        // Try online first, then offline (name or SteamID)
        bool isOnline = ModerationHelpers.TryFindUser(em, playerName, out var ue, out var user);
        if (!isOnline && !ModerationHelpers.TryFindUserOffline(em, playerName, out ue, out user))
        {
            ctx.Reply($"Игрок '{playerName}' не найден ни онлайн, ни в истории сервера.");
            return;
        }

        var steamId  = user.PlatformId.ToString();
        var charName = user.CharacterName.Value;

        BanDatabase.AddBan(new BanEntry
        {
            SteamId   = steamId,
            Name      = charName,
            Reason    = reason,
            BannedBy  = by,
            BannedAt  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = expiresAt,
        });

        var durDisplay = expiresAt.HasValue
            ? TimeSpan.FromSeconds(expiresAt.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(@"d\d\ h\h")
            : "навсегда";

        var offlineSuffix = isOnline ? "" : " (оффлайн)";
        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{charName}</color> забанен админом <color=#00ccff>{by}</color> на <color=#ffffff>{durDisplay}</color>{offlineSuffix}. Причина: <color=#ff8800>{reason}</color>");

        if (isOnline)
            ModerationHelpers.KickUser(ue, em, $"Бан на {durDisplay}. Причина: {reason}", expiresAt ?? 0L);

        ctx.Reply($"Banned {charName} ({steamId}) for {durDisplay}{offlineSuffix}");

        ChatHooks.PendingEvents.Enqueue(new ServerEvent(
            "moderation", by, "ban",
            $"Banned {charName} ({steamId}) for {durDisplay}{offlineSuffix}: {reason}",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .unban SteamID ───────────────────────────────────────────────────────
    [Command("unban", description: "Unban a player by SteamID")]
    public void Unban(ChatCommandContext ctx, string steamId = "")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(steamId))
        {
            ctx.Reply("Использование: .unban <SteamID>");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        BanDatabase.Unban(steamId);
        ModerationHelpers.BroadcastMessage(em, $"<color=#44ff44>*</color> SteamID <color=#ffcc00>{steamId}</color> был разбанен админом <color=#00ccff>{by}</color>.");
        ctx.Reply($"Unbanned SteamID {steamId}");

        ChatHooks.PendingEvents.Enqueue(new ServerEvent(
            "moderation", by, "unban",
            $"Unbanned SteamID {steamId}",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .mute PlayerName Duration [Reason] ───────────────────────────────────
    [Command("mute", description: "Mute a player")]
    public void Mute(ChatCommandContext ctx, string playerName = "", string duration = "", string reason = "muted by admin")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(duration))
        {
            ctx.Reply("Использование: .mute <игрок> <длительность> [причина]");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        if (!ModerationHelpers.TryFindUser(em, playerName, out _, out var user))
        {
            ctx.Reply($"Игрок '{playerName}' не найден онлайн.");
            return;
        }

        var steamId  = user.PlatformId.ToString();
        var charName = user.CharacterName.Value;
        var expiresAt = BanDatabase.ParseDuration(duration);

        MuteDatabase.AddMute(new MuteEntry
        {
            SteamId   = steamId,
            Name      = charName,
            Reason    = reason,
            MutedBy   = by,
            MutedAt   = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = expiresAt,
        });

        var durDisplay = expiresAt.HasValue
            ? TimeSpan.FromSeconds(expiresAt.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(@"d\d\ h\h")
            : "permanent";

        ModerationHelpers.BroadcastMessage(em, $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{charName}</color> замьючен админом <color=#00ccff>{by}</color> на <color=#ffffff>{durDisplay}</color>. Причина: <color=#ff8800>{reason}</color>");
        ctx.Reply($"Muted {charName} ({steamId}) for {durDisplay}");

        ChatHooks.PendingEvents.Enqueue(new ServerEvent(
            "moderation", by, "mute",
            $"Muted {charName} ({steamId}) for {durDisplay}: {reason}",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .unmute PlayerName ────────────────────────────────────────────────────
    [Command("unmute", description: "Unmute a player")]
    public void Unmute(ChatCommandContext ctx, string playerName = "")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(playerName))
        {
            ctx.Reply("Использование: .unmute <игрок>");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        if (!ModerationHelpers.TryFindUser(em, playerName, out _, out var user))
        {
            ctx.Reply($"Игрок '{playerName}' не найден онлайн.");
            return;
        }

        var steamId  = user.PlatformId.ToString();
        var charName = user.CharacterName.Value;

        MuteDatabase.Unmute(steamId);
        ModerationHelpers.BroadcastMessage(em, $"<color=#44ff44>*</color> Игрок <color=#ffcc00>{charName}</color> размьючен админом <color=#00ccff>{by}</color>.");
        ctx.Reply($"Unmuted {charName} ({steamId})");

        ChatHooks.PendingEvents.Enqueue(new ServerEvent(
            "moderation", by, "unmute",
            $"Unmuted {charName} ({steamId})",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .warn PlayerName [Reason] ─────────────────────────────────────────────
    [Command("warn", description: "Warn a player (3 warns = auto-ban)")]
    public void Warn(ChatCommandContext ctx, string playerName = "", string reason = "warned by admin")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(playerName))
        {
            ctx.Reply("Использование: .warn <игрок> [причина]");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        if (!ModerationHelpers.TryFindUser(em, playerName, out var ue, out var user))
        {
            ctx.Reply($"Игрок '{playerName}' не найден онлайн.");
            return;
        }

        var steamId  = user.PlatformId.ToString();
        var charName = user.CharacterName.Value;

        WarnDatabase.AddWarn(new WarnEntry
        {
            SteamId  = steamId,
            Name     = charName,
            Reason   = reason,
            WarnedBy = by,
            WarnedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });

        var count = WarnDatabase.GetWarnCount(steamId);

        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ffaa00>*</color> Игрок <color=#ffcc00>{charName}</color> получил предупреждение от админа <color=#00ccff>{by}</color> (<color=#ffffff>{count}/{WarnDatabase.AutoBanThreshold}</color>). Причина: <color=#ff8800>{reason}</color>");

        ChatHooks.PendingEvents.Enqueue(new ServerEvent(
            "moderation", by, "warn",
            $"Warned {charName} ({steamId}) [{count}/{WarnDatabase.AutoBanThreshold}]: {reason}",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        // Auto-ban at threshold
        if (count >= WarnDatabase.AutoBanThreshold)
        {
            BanDatabase.AddBan(new BanEntry
            {
                SteamId  = steamId,
                Name     = charName,
                Reason   = $"Автобан: {count} предупреждений",
                BannedBy = "system",
                BannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ExpiresAt = null, // permanent
            });

            ModerationHelpers.BroadcastMessage(em,
                $"<color=#ff0000>*</color> Игрок <color=#ffcc00>{charName}</color> <color=#ff0000>автоматически забанен</color> за {count} предупреждений!");
            ModerationHelpers.KickUser(ue, em, $"Автобан: {count} предупреждений", 0L);
            WarnDatabase.ClearWarns(steamId);

            ctx.Reply($"Auto-banned {charName} ({steamId}) — {count} warnings reached.");
        }
        else
        {
            ctx.Reply($"Warned {charName} ({steamId}) — {count}/{WarnDatabase.AutoBanThreshold}");
        }
    }

    // ── .clearwarns PlayerName ────────────────────────────────────────────────
    [Command("clearwarns", description: "Clear all warnings for a player")]
    public void ClearWarns(ChatCommandContext ctx, string playerName = "")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(playerName))
        {
            ctx.Reply("Использование: .clearwarns <игрок>");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        if (!ModerationHelpers.TryFindUser(em, playerName, out _, out var user))
        {
            ctx.Reply($"Игрок '{playerName}' не найден онлайн.");
            return;
        }

        var steamId  = user.PlatformId.ToString();
        var charName = user.CharacterName.Value;
        WarnDatabase.ClearWarns(steamId);
        ctx.Reply($"Предупреждения {charName} ({steamId}) очищены.");
    }

    // ── .announce Message ─────────────────────────────────────────────────────
    [Command("announce", description: "Broadcast an announcement")]
    public void Announce(ChatCommandContext ctx, string message = "")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            ctx.Reply("Использование: .announce <текст>");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ff5555>━━━━━━━━━━━━━━━━━━━━━━━━</color>\n<color=#55ff55>📢 ОБЪЯВЛЕНИЕ:</color> <color=#ffffff>{message}</color>\n<color=#ff5555>━━━━━━━━━━━━━━━━━━━━━━━━</color>");
        ctx.Reply("Announcement sent.");
    }

    // ── .online ───────────────────────────────────────────────────────────────
    [Command("online", description: "List online players")]
    public void Online(ChatCommandContext ctx)
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        var qb    = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        var query = qb.Build(em);

        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            int count = 0;
            ctx.Reply("<color=#00ccff>━━━ Онлайн игроки ━━━</color>");

            foreach (var e in entities)
            {
                try
                {
                    var u = em.GetComponentData<User>(e);
                    if (!u.IsConnected) continue;
                    count++;
                    var name    = u.CharacterName.Value;
                    var steam   = u.PlatformId.ToString();
                    var isAdmin = u.IsAdmin ? " <color=#ff5555>[A]</color>" : "";
                    ctx.Reply($"<color=#ffcc00>{name}</color> <color=#888888>({steam})</color>{isAdmin}");
                }
                catch { }
            }
            entities.Dispose();

            ctx.Reply($"<color=#00ccff>Всего: {count}</color>");
        }
        finally
        {
            query.Dispose();
            qb.Dispose();
        }
    }

    // ── .banlist ──────────────────────────────────────────────────────────────
    [Command("banlist", description: "Show active bans")]
    public void BanList(ChatCommandContext ctx)
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }

        var bans = BanDatabase.GetAll();
        if (bans.Count == 0)
        {
            ctx.Reply("<color=#44ff44>Нет активных банов.</color>");
            return;
        }

        ctx.Reply($"<color=#ff4444>━━━ Активные баны ({bans.Count}) ━━━</color>");
        foreach (var b in bans)
        {
            var expiry = b.ExpiresAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(b.ExpiresAt.Value).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm")
                : "навсегда";
            ctx.Reply($"<color=#ffcc00>{b.Name}</color> <color=#888888>({b.SteamId})</color> до <color=#ffffff>{expiry}</color> — {b.Reason}");
        }
    }

    // ── .mutelist ─────────────────────────────────────────────────────────────
    [Command("mutelist", description: "Show active mutes")]
    public void MuteList(ChatCommandContext ctx)
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }

        var mutes = MuteDatabase.GetAll();
        if (mutes.Count == 0)
        {
            ctx.Reply("<color=#44ff44>Нет активных мутов.</color>");
            return;
        }

        ctx.Reply($"<color=#ff8800>━━━ Активные муты ({mutes.Count}) ━━━</color>");
        foreach (var m in mutes)
        {
            var expiry = m.ExpiresAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(m.ExpiresAt.Value).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm")
                : "навсегда";
            ctx.Reply($"<color=#ffcc00>{m.Name}</color> <color=#888888>({m.SteamId})</color> до <color=#ffffff>{expiry}</color> — {m.Reason}");
        }
    }

    // ── .warnlist [PlayerName] ────────────────────────────────────────────────
    [Command("warnlist", description: "Show warnings for a player or all")]
    public void WarnList(ChatCommandContext ctx, string playerName = "")
    {
        if (!ctx.Event.User.IsAdmin)
        {
            ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
            return;
        }

        List<WarnEntry> warns;
        if (string.IsNullOrEmpty(playerName))
        {
            warns = WarnDatabase.GetAll();
        }
        else
        {
            var world = ModerationHelpers.GetServerWorld();
            if (world == null) { ctx.Reply("Server world not ready."); return; }
            var em = world.EntityManager;

            if (ModerationHelpers.TryFindUser(em, playerName, out _, out var user))
                warns = WarnDatabase.GetWarnsForPlayer(user.PlatformId.ToString());
            else
                warns = WarnDatabase.GetAll().Where(w => w.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (warns.Count == 0)
        {
            ctx.Reply("<color=#44ff44>Нет предупреждений.</color>");
            return;
        }

        ctx.Reply($"<color=#ffaa00>━━━ Предупреждения ({warns.Count}) ━━━</color>");
        foreach (var w in warns)
        {
            var date = DateTimeOffset.FromUnixTimeSeconds(w.WarnedAt).ToString("dd.MM HH:mm");
            ctx.Reply($"<color=#ffcc00>{w.Name}</color> <color=#888888>[{date}]</color> от <color=#00ccff>{w.WarnedBy}</color> — {w.Reason}");
        }
    }
}

/// <summary>
/// Shared helpers used by VCF commands, MapPushCoroutine (remote commands),
/// and ChatHooks (ban-on-login check).
/// </summary>
public static class ModerationHelpers
{
    public static World? GetServerWorld()
    {
        if (World.s_AllWorlds == null) return null;
        foreach (var w in World.s_AllWorlds)
            if (w != null && w.Name == "Server") return w;
        return null;
    }

    /// <summary>
    /// Called from ServerBootstrapSystemPatch when a player connects.
    /// If they are banned, sends a detailed notification and kicks them immediately.
    /// </summary>
    public static bool CheckBanOnConnect(Entity userEntity, EntityManager em)
    {
        try
        {
            var user    = em.GetComponentData<User>(userEntity);
            var steamId = user.PlatformId.ToString();
            var ban     = BanDatabase.GetActiveBan(steamId);
            if (ban == null) return false;

            var expMsg = ban.ExpiresAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(ban.ExpiresAt.Value).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy HH:mm") + " MSK"
                : "навсегда";

            Plugin.Logger.LogInfo(
                $"[JSMonitor] Banned player {user.CharacterName.Value} ({steamId}) tried to connect — kicking. Ban until {expMsg}. Reason: {ban.Reason}");

            // Send detailed ban notification before kicking
            SendMessageToUser(em, userEntity,
                $"<color=#ff0000>━━━━━━━━━━━━━━━━━━━━━━━━</color>\n" +
                $"<color=#ff0000>⛔ ВЫ ЗАБАНЕНЫ НА ЭТОМ СЕРВЕРЕ</color>\n" +
                $"<color=#ffaa00>Срок бана:</color> <color=#ffffff>{expMsg}</color>\n" +
                $"<color=#ffaa00>Причина:</color> <color=#ffffff>{ban.Reason}</color>\n" +
                $"<color=#ffaa00>Забанил:</color> <color=#ffffff>{ban.BannedBy}</color>\n" +
                $"<color=#ff0000>━━━━━━━━━━━━━━━━━━━━━━━━</color>");

            KickUser(userEntity, em);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[JSMonitor] Ban-check error: {ex.Message}");
            return false;
        }
    }

    // ── Public API (used by MapPushCoroutine for remote commands) ─────────────

    public static bool TryFindUser(EntityManager em, string name, out Entity userEntity, out User user)
    {
        userEntity = Entity.Null;
        user       = default;

        var qb    = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        var query = qb.Build(em);

        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                try
                {
                    var u = em.GetComponentData<User>(e);
                    if (!u.IsConnected) continue;
                    if (u.CharacterName.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        userEntity = e;
                        user       = u;
                        entities.Dispose();
                        return true;
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
        return false;
    }

    /// <summary>
    /// Searches ALL users (including offline) by character name or SteamID (17 digits).
    /// </summary>
    public static bool TryFindUserOffline(EntityManager em, string nameOrSteamId, out Entity userEntity, out User user)
    {
        userEntity = Entity.Null;
        user       = default;

        bool isSteamId = nameOrSteamId.Length == 17 && nameOrSteamId.All(char.IsDigit);

        var qb    = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        var query = qb.Build(em);

        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                try
                {
                    var u = em.GetComponentData<User>(e);
                    bool match = isSteamId
                        ? u.PlatformId.ToString() == nameOrSteamId
                        : u.CharacterName.Value.Equals(nameOrSteamId, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        userEntity = e;
                        user       = u;
                        entities.Dispose();
                        return true;
                    }
                }
                catch { }
            }
            entities.Dispose();
        }
        finally { query.Dispose(); qb.Dispose(); }
        return false;
    }

    /// <summary>
    /// Forces a connected user to disconnect.
    /// Sends an optional kick reason to the player before disconnecting.
    /// Tries multiple approaches: KickBanSystem via reflection, then direct disconnect.
    /// </summary>
    public static void KickUser(Entity userEntity, EntityManager em, string? reason = null, long banExpirationTick = 0)
    {
        try
        {
            // ── Send kick notification to the player before disconnecting ────
            if (reason != null)
            {
                SendMessageToUser(em, userEntity,
                    $"<color=#ff4444>━━━━━━━━━━━━━━━━━━━━━━━━</color>\n" +
                    $"<color=#ff4444>Вы были кикнуты с сервера</color>\n" +
                    $"<color=#ff8800>Причина: {reason}</color>\n" +
                    $"<color=#ff4444>━━━━━━━━━━━━━━━━━━━━━━━━</color>");
            }

            var world = GetServerWorld();
            if (world == null)
            {
                Plugin.Logger.LogWarning("[JSMonitor] KickUser: Server world is null.");
                return;
            }

            var networkId = em.GetComponentData<NetworkId>(userEntity);
            Plugin.Logger.LogInfo($"[JSMonitor] KickUser: Attempting to kick NetworkId={networkId}");

            var bindAll = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

            // ── Approach 1: ServerBootstrapSystem via GetExistingSystemManaged ─────────
            var platformId = em.GetComponentData<User>(userEntity).PlatformId;
            var sbs = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (sbs != null)
            {
                // Find ConnectionStatusChangeReason type dynamically
                Type? cscrType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    cscrType = asm.GetType("ProjectM.Network.ConnectionStatusChangeReason");
                    if (cscrType != null) break;
                }

                if (cscrType != null)
                {
                    var kickMethod = sbs.GetType().GetMethod("Kick", bindAll, null,
                        new[] { typeof(ulong), cscrType, typeof(bool), typeof(long) }, null);
                    if (kickMethod != null)
                    {
                        var reasonName = banExpirationTick > 0 ? "Banned" : "Kicked";
                        object reasonVal;
                        try { reasonVal = System.Enum.Parse(cscrType, reasonName); }
                        catch { reasonVal = System.Enum.ToObject(cscrType, banExpirationTick > 0 ? 3 : 1); }
                        Plugin.Logger.LogInfo($"[JSMonitor] Invoking ServerBootstrapSystem.Kick(platformId={platformId}, reason={reasonVal}, banTick={banExpirationTick})");
                        kickMethod.Invoke(sbs, new object[] { platformId, reasonVal, true, banExpirationTick });
                        return;
                    }

                    // Log available Kick overloads to help diagnose signature mismatches
                    foreach (var m in sbs.GetType().GetMethods(bindAll))
                        if (m.Name == "Kick")
                            Plugin.Logger.LogInfo($"[JSMonitor] Kick overload: ({string.Join(", ", System.Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"))})");
                    Plugin.Logger.LogWarning("[JSMonitor] ServerBootstrapSystem.Kick — signature mismatch, see overloads above.");
                }
                else
                {
                    Plugin.Logger.LogWarning("[JSMonitor] ConnectionStatusChangeReason type not found.");
                }
            }
            else
            {
                Plugin.Logger.LogWarning("[JSMonitor] GetExistingSystemManaged<ServerBootstrapSystem>() returned null.");
            }

            Plugin.Logger.LogWarning("[JSMonitor] KickUser: falling back to IsConnected = false.");

            // ── Approach 2: direct User.IsConnected = false ───────────────────
            Plugin.Logger.LogInfo("[JSMonitor] KickUser: Trying direct User.IsConnected = false");
            var userData = em.GetComponentData<User>(userEntity);
            userData.IsConnected = false;
            em.SetComponentData(userEntity, userData);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[JSMonitor] KickUser error: {ex.Message}");
        }
    }

    /// <summary>Sends a server-system message to a specific user.</summary>
    public static void SendMessageToUser(EntityManager em, Entity userEntity, string text)
    {
        try
        {
            var user = em.GetComponentData<User>(userEntity);
            var msg  = new FixedString512Bytes(text);
            ServerChatUtils.SendSystemMessageToClient(em, user, ref msg);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[JSMonitor] SendMessageToUser error: {ex.Message}");
        }
    }

    /// <summary>Sends a server-system message to all connected clients.</summary>
    public static void BroadcastMessage(EntityManager em, string text)
    {
        try
        {
            var msg = new FixedString512Bytes(text);
            ServerChatUtils.SendSystemMessageToAllClients(em, ref msg);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[JSMonitor] BroadcastMessage error: {ex.Message}");
        }
    }
}
