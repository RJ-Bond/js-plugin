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
/// VCF chat commands for moderation.
/// Commands:
///   .kick  Player [reason...]
///   .ban   Player Duration [reason...]    (duration: 30m / 2h / 7d / 0 = permanent)
///   .unban Player|SteamID
///   .mute  Player Duration [reason...]
///   .unmute Player|SteamID
///   .warn  Player [reason...]
///   .clearwarns Player
///   .announce Message...
///   .online
///   .banlist / .mutelist / .warnlist [Player]
///   .info  Player|SteamID
///   .history Player|SteamID
///   .chatfilter list|add|remove [word]
/// </summary>
public class ModerationVCFCommands
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    static bool IsAdmin(ChatCommandContext ctx)
    {
        if (ctx.Event.User.IsAdmin) return true;
        ctx.Reply("<color=red>* Вы не имеете права доступа к этой команде</color>");
        return false;
    }

    /// <summary>Join up to 5 extra reason words; use fallback if all empty.</summary>
    static string BuildReason(string w0, string w1, string w2, string w3, string w4, string fallback)
    {
        var r = string.Join(" ", new[] { w0, w1, w2, w3, w4 }
            .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        return r.Length > 0 ? r : fallback;
    }

    /// <summary>Remaining time as "7д 3ч", "45м", "навсегда".</summary>
    static string FormatRemaining(long? expiresAt)
    {
        if (!expiresAt.HasValue) return "навсегда";
        var sec = expiresAt.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (sec <= 0) return "истёк";
        var ts = TimeSpan.FromSeconds(sec);
        if (ts.TotalDays >= 1)  return $"{(int)ts.TotalDays}д {ts.Hours}ч";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}ч {ts.Minutes}м";
        return $"{(int)ts.TotalMinutes}м";
    }

    /// <summary>Expiry date + remaining: "09.03.2026 15:00 (осталось 2д 3ч)".</summary>
    static string FormatExpiry(long? expiresAt)
    {
        if (!expiresAt.HasValue) return "навсегда";
        var dt = DateTimeOffset.FromUnixTimeSeconds(expiresAt.Value)
                               .ToOffset(TimeSpan.FromHours(3));
        return $"{dt:dd.MM.yyyy HH:mm} (осталось {FormatRemaining(expiresAt)})";
    }

    /// <summary>
    /// Finds an online player by exact or partial name.
    /// Returns false and sends a reply if 0 or ambiguous matches.
    /// </summary>
    static bool TryFindOnline(EntityManager em, string input,
        out Entity ue, out User user, ChatCommandContext ctx)
    {
        ue = Entity.Null; user = default;

        // Exact match first
        if (ModerationHelpers.TryFindUser(em, input, out ue, out user)) return true;

        // Partial name search
        var matches = new List<(Entity e, User u)>();
        var qb = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        var q = qb.Build(em);
        try
        {
            var entities = q.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                try
                {
                    var u = em.GetComponentData<User>(e);
                    if (!u.IsConnected) continue;
                    if (u.CharacterName.Value.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add((e, u));
                }
                catch { }
            }
            entities.Dispose();
        }
        finally { q.Dispose(); qb.Dispose(); }

        if (matches.Count == 1) { (ue, user) = matches[0]; return true; }
        if (matches.Count > 1)
            ctx.Reply($"<color=#ffaa00>Несколько совпадений: {string.Join(", ", matches.Select(m => m.u.CharacterName.Value))}. Уточните имя.</color>");
        else
            ctx.Reply($"<color=#ffaa00>Игрок '{input}' не найден онлайн.</color>");
        return false;
    }

    // ── .kick ─────────────────────────────────────────────────────────────────
    [Command("kick", description: "Kick: .kick <player> [reason...]")]
    public void Kick(ChatCommandContext ctx, string playerName = "",
        string r0 = "", string r1 = "", string r2 = "", string r3 = "", string r4 = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName)) { ctx.Reply("Использование: .kick <игрок> [причина]"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        if (!TryFindOnline(em, playerName, out var ue, out var user, ctx)) return;

        var charName = user.CharacterName.Value;
        var steamId  = user.PlatformId.ToString();

        if (charName.Equals(by, StringComparison.OrdinalIgnoreCase))
        { ctx.Reply("<color=#ffaa00>Вы не можете кикнуть себя.</color>"); return; }

        var reason = BuildReason(r0, r1, r2, r3, r4, "kicked by admin");

        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{charName}</color> был кикнут админом <color=#00ccff>{by}</color>. Причина: <color=#ff8800>{reason}</color>");
        ModerationHelpers.KickUser(ue, em, reason);
        ctx.Reply($"Kicked {charName} ({steamId})");

        ModlogDatabase.Log(new ModlogEntry { Action = "kick", SteamId = steamId, Name = charName, Reason = reason, By = by, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        ChatHooks.PendingEvents.Enqueue(new ServerEvent("moderation", by, "kick", $"Kicked {charName} ({steamId}): {reason}", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .ban ──────────────────────────────────────────────────────────────────
    [Command("ban", description: "Ban: .ban <player|SteamID> <duration> [reason...]  (30m/2h/7d/0=perm)")]
    public void Ban(ChatCommandContext ctx, string playerName = "", string duration = "",
        string r0 = "", string r1 = "", string r2 = "", string r3 = "", string r4 = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(duration))
        { ctx.Reply("Использование: .ban <игрок|SteamID> <длительность> [причина]  (пример: .ban Vasya 1d читерство)"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        bool isOnline = ModerationHelpers.TryFindUser(em, playerName, out var ue, out var user);
        if (!isOnline && !ModerationHelpers.TryFindUserOffline(em, playerName, out ue, out user))
        { ctx.Reply($"<color=#ffaa00>Игрок '{playerName}' не найден ни онлайн, ни в истории сервера.</color>"); return; }

        var charName = user.CharacterName.Value;
        var steamId  = user.PlatformId.ToString();

        if (charName.Equals(by, StringComparison.OrdinalIgnoreCase))
        { ctx.Reply("<color=#ffaa00>Вы не можете забанить себя.</color>"); return; }

        var reason    = BuildReason(r0, r1, r2, r3, r4, "banned by admin");
        var expiresAt = BanDatabase.ParseDuration(duration);
        var durStr    = FormatRemaining(expiresAt);
        var offSuffix = isOnline ? "" : " (оффлайн)";

        BanDatabase.AddBan(new BanEntry { SteamId = steamId, Name = charName, Reason = reason, BannedBy = by, BannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ExpiresAt = expiresAt });
        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{charName}</color> забанен админом <color=#00ccff>{by}</color> на <color=#ffffff>{durStr}</color>{offSuffix}. Причина: <color=#ff8800>{reason}</color>");

        if (isOnline)
            ModerationHelpers.KickUser(ue, em, $"Бан на {durStr}. Причина: {reason}", expiresAt ?? 0L);

        ctx.Reply($"Banned {charName} ({steamId}) for {durStr}{offSuffix}");

        ModlogDatabase.Log(new ModlogEntry { Action = "ban", SteamId = steamId, Name = charName, Reason = reason, By = by, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ExpiresAt = expiresAt });
        ChatHooks.PendingEvents.Enqueue(new ServerEvent("moderation", by, "ban", $"Banned {charName} ({steamId}) for {durStr}{offSuffix}: {reason}", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .unban ────────────────────────────────────────────────────────────────
    [Command("unban", description: "Unban: .unban <player|SteamID>")]
    public void Unban(ChatCommandContext ctx, string playerName = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName)) { ctx.Reply("Использование: .unban <игрок|SteamID>"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        var entry = BanDatabase.UnbanByNameOrSteamId(playerName);
        if (entry == null) { ctx.Reply($"<color=#ffaa00>Бан для '{playerName}' не найден.</color>"); return; }

        ModerationHelpers.BroadcastMessage(em,
            $"<color=#44ff44>*</color> Игрок <color=#ffcc00>{entry.Name}</color> разбанен админом <color=#00ccff>{by}</color>.");
        ctx.Reply($"Unbanned {entry.Name} ({entry.SteamId})");

        ModlogDatabase.Log(new ModlogEntry { Action = "unban", SteamId = entry.SteamId, Name = entry.Name, By = by, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        ChatHooks.PendingEvents.Enqueue(new ServerEvent("moderation", by, "unban", $"Unbanned {entry.Name} ({entry.SteamId})", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .mute ─────────────────────────────────────────────────────────────────
    [Command("mute", description: "Mute: .mute <player> <duration> [reason...]")]
    public void Mute(ChatCommandContext ctx, string playerName = "", string duration = "",
        string r0 = "", string r1 = "", string r2 = "", string r3 = "", string r4 = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(duration))
        { ctx.Reply("Использование: .mute <игрок> <длительность> [причина]"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        if (!TryFindOnline(em, playerName, out _, out var user, ctx)) return;

        var charName  = user.CharacterName.Value;
        var steamId   = user.PlatformId.ToString();

        if (charName.Equals(by, StringComparison.OrdinalIgnoreCase))
        { ctx.Reply("<color=#ffaa00>Вы не можете замьютить себя.</color>"); return; }

        var reason    = BuildReason(r0, r1, r2, r3, r4, "muted by admin");
        var expiresAt = BanDatabase.ParseDuration(duration);
        var durStr    = FormatRemaining(expiresAt);

        MuteDatabase.AddMute(new MuteEntry { SteamId = steamId, Name = charName, Reason = reason, MutedBy = by, MutedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ExpiresAt = expiresAt });
        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ff4444>*</color> Игрок <color=#ffcc00>{charName}</color> замьючен админом <color=#00ccff>{by}</color> на <color=#ffffff>{durStr}</color>. Причина: <color=#ff8800>{reason}</color>");
        ctx.Reply($"Muted {charName} ({steamId}) for {durStr}");

        ModlogDatabase.Log(new ModlogEntry { Action = "mute", SteamId = steamId, Name = charName, Reason = reason, By = by, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ExpiresAt = expiresAt });
        ChatHooks.PendingEvents.Enqueue(new ServerEvent("moderation", by, "mute", $"Muted {charName} ({steamId}) for {durStr}: {reason}", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .unmute ───────────────────────────────────────────────────────────────
    [Command("unmute", description: "Unmute: .unmute <player|SteamID>  (works offline)")]
    public void Unmute(ChatCommandContext ctx, string playerName = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName)) { ctx.Reply("Использование: .unmute <игрок|SteamID>"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        var entry = MuteDatabase.UnmuteByNameOrSteamId(playerName);
        if (entry == null) { ctx.Reply($"<color=#ffaa00>Мут для '{playerName}' не найден.</color>"); return; }

        ModerationHelpers.BroadcastMessage(em,
            $"<color=#44ff44>*</color> Игрок <color=#ffcc00>{entry.Name}</color> размьючен админом <color=#00ccff>{by}</color>.");
        ctx.Reply($"Unmuted {entry.Name} ({entry.SteamId})");

        ModlogDatabase.Log(new ModlogEntry { Action = "unmute", SteamId = entry.SteamId, Name = entry.Name, By = by, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        ChatHooks.PendingEvents.Enqueue(new ServerEvent("moderation", by, "unmute", $"Unmuted {entry.Name} ({entry.SteamId})", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ── .warn ─────────────────────────────────────────────────────────────────
    [Command("warn", description: "Warn: .warn <player> [reason...]  (3 warns = auto-ban)")]
    public void Warn(ChatCommandContext ctx, string playerName = "",
        string r0 = "", string r1 = "", string r2 = "", string r3 = "", string r4 = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName)) { ctx.Reply("Использование: .warn <игрок> [причина]"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        if (!TryFindOnline(em, playerName, out var ue, out var user, ctx)) return;

        var charName = user.CharacterName.Value;
        var steamId  = user.PlatformId.ToString();
        var reason   = BuildReason(r0, r1, r2, r3, r4, "warned by admin");

        WarnDatabase.AddWarn(new WarnEntry { SteamId = steamId, Name = charName, Reason = reason, WarnedBy = by, WarnedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        var count = WarnDatabase.GetWarnCount(steamId);

        ModerationHelpers.BroadcastMessage(em,
            $"<color=#ffaa00>*</color> Игрок <color=#ffcc00>{charName}</color> получил предупреждение от <color=#00ccff>{by}</color> (<color=#ffffff>{count}/{WarnDatabase.AutoBanThreshold}</color>). Причина: <color=#ff8800>{reason}</color>");

        ModlogDatabase.Log(new ModlogEntry { Action = "warn", SteamId = steamId, Name = charName, Reason = reason, By = by, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        ChatHooks.PendingEvents.Enqueue(new ServerEvent("moderation", by, "warn", $"Warned {charName} ({steamId}) [{count}/{WarnDatabase.AutoBanThreshold}]: {reason}", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        if (count >= WarnDatabase.AutoBanThreshold)
        {
            BanDatabase.AddBan(new BanEntry { SteamId = steamId, Name = charName, Reason = $"Автобан: {count} предупреждений", BannedBy = "system", BannedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ExpiresAt = null });
            ModerationHelpers.BroadcastMessage(em,
                $"<color=#ff0000>*</color> Игрок <color=#ffcc00>{charName}</color> <color=#ff0000>автоматически забанен</color> за {count} предупреждений!");
            ModerationHelpers.KickUser(ue, em, $"Автобан: {count} предупреждений", 0L);
            WarnDatabase.ClearWarns(steamId);
            ModlogDatabase.Log(new ModlogEntry { Action = "ban", SteamId = steamId, Name = charName, Reason = $"Автобан: {count} предупреждений", By = "system", At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            ctx.Reply($"Auto-banned {charName} ({steamId}) — {count} warnings reached.");
        }
        else
        {
            ctx.Reply($"Warned {charName} ({steamId}) — {count}/{WarnDatabase.AutoBanThreshold}");
        }
    }

    // ── .clearwarns ───────────────────────────────────────────────────────────
    [Command("clearwarns", description: "Clear all warnings: .clearwarns <player|SteamID>")]
    public void ClearWarns(ChatCommandContext ctx, string playerName = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName)) { ctx.Reply("Использование: .clearwarns <игрок|SteamID>"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;
        var by = ctx.Event.User.CharacterName.Value;

        // Try online first, then offline, then treat input as SteamID directly
        string steamId, charName;
        if (ModerationHelpers.TryFindUser(em, playerName, out _, out var u))
        { steamId = u.PlatformId.ToString(); charName = u.CharacterName.Value; }
        else if (ModerationHelpers.TryFindUserOffline(em, playerName, out _, out u))
        { steamId = u.PlatformId.ToString(); charName = u.CharacterName.Value; }
        else
        { ctx.Reply($"<color=#ffaa00>Игрок '{playerName}' не найден.</color>"); return; }

        WarnDatabase.ClearWarns(steamId);
        ModlogDatabase.Log(new ModlogEntry { Action = "clearwarns", SteamId = steamId, Name = charName, By = by, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        ctx.Reply($"Предупреждения {charName} ({steamId}) очищены.");
    }

    // ── .announce ─────────────────────────────────────────────────────────────
    [Command("announce", description: "Broadcast: .announce <text...>")]
    public void Announce(ChatCommandContext ctx,
        string w0 = "", string w1 = "", string w2 = "", string w3 = "", string w4 = "",
        string w5 = "", string w6 = "", string w7 = "", string w8 = "", string w9 = "")
    {
        if (!IsAdmin(ctx)) return;
        var message = string.Join(" ", new[] { w0, w1, w2, w3, w4, w5, w6, w7, w8, w9 }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(message)) { ctx.Reply("Использование: .announce <текст>"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        ModerationHelpers.BroadcastMessage(world.EntityManager,
            $"<color=#ff5555>━━━━━━━━━━━━━━━━━━━━━━━━</color>\n<color=#55ff55>[!] ОБЪЯВЛЕНИЕ:</color> <color=#ffffff>{message}</color>\n<color=#ff5555>━━━━━━━━━━━━━━━━━━━━━━━━</color>");
        ctx.Reply("Announcement sent.");
    }

    // ── .online ───────────────────────────────────────────────────────────────
    [Command("online", description: "List online players")]
    public void Online(ChatCommandContext ctx)
    {
        if (!IsAdmin(ctx)) return;

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        var qb = new EntityQueryBuilder(Allocator.Temp);
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
                    var adm = u.IsAdmin ? " <color=#ff5555>[A]</color>" : "";
                    ctx.Reply($"<color=#ffcc00>{u.CharacterName.Value}</color> <color=#888888>({u.PlatformId})</color>{adm}");
                }
                catch { }
            }
            entities.Dispose();
            ctx.Reply($"<color=#00ccff>Всего: {count}</color>");
        }
        finally { query.Dispose(); qb.Dispose(); }
    }

    // ── .a (admin chat) ───────────────────────────────────────────────────────
    [Command("a", description: "Admin chat: .a <text...>  (visible to admins only)")]
    public void AdminChat(ChatCommandContext ctx,
        string w0 = "", string w1 = "", string w2 = "", string w3 = "", string w4 = "",
        string w5 = "", string w6 = "", string w7 = "", string w8 = "", string w9 = "")
    {
        if (!IsAdmin(ctx)) return;
        var message = string.Join(" ", new[] { w0, w1, w2, w3, w4, w5, w6, w7, w8, w9 }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        if (string.IsNullOrWhiteSpace(message)) { ctx.Reply("Использование: .a <текст>"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        var sender = ctx.Event.User.CharacterName.Value;
        var formatted = $"<color=#ff5555>[ADMIN]</color> <color=#ffcc00>{sender}</color><color=#888888>:</color> <color=#ffffff>{message}</color>";

        int sent = ModerationHelpers.SendMessageToAdmins(em, formatted);
        Plugin.Logger.LogInfo($"[JSMonitor] AdminChat from {sender} ({sent} recipients): {message}");
    }

    // ── .admins ───────────────────────────────────────────────────────────────
    [Command("admins", description: "List online admins")]
    public void Admins(ChatCommandContext ctx)
    {
        if (!IsAdmin(ctx)) return;

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        var qb = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<User>());
        var query = qb.Build(em);
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            var admins = new List<string>();
            foreach (var e in entities)
            {
                try
                {
                    var u = em.GetComponentData<User>(e);
                    if (u.IsConnected && u.IsAdmin)
                        admins.Add(u.CharacterName.Value);
                }
                catch { }
            }
            entities.Dispose();

            if (admins.Count == 0)
                ctx.Reply("<color=#ffaa00>Нет администраторов онлайн.</color>");
            else
            {
                ctx.Reply($"<color=#ff5555>━━━ Администраторы онлайн ({admins.Count}) ━━━</color>");
                foreach (var name in admins)
                    ctx.Reply($"<color=#ffcc00>{name}</color>");
            }
        }
        finally { query.Dispose(); qb.Dispose(); }
    }

    // ── .info ─────────────────────────────────────────────────────────────────
    [Command("info", description: "Player info: .info <player|SteamID>")]
    public void Info(ChatCommandContext ctx, string playerName = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName)) { ctx.Reply("Использование: .info <игрок|SteamID>"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        bool isOnline = ModerationHelpers.TryFindUser(em, playerName, out _, out var user);
        if (!isOnline && !ModerationHelpers.TryFindUserOffline(em, playerName, out _, out user))
        { ctx.Reply($"<color=#ffaa00>Игрок '{playerName}' не найден.</color>"); return; }

        var charName = user.CharacterName.Value;
        var steamId  = user.PlatformId.ToString();
        var status   = isOnline ? "<color=#44ff44>онлайн</color>" : "<color=#888888>оффлайн</color>";
        var adminTag = user.IsAdmin ? " <color=#ff5555>[ADMIN]</color>" : "";

        ctx.Reply($"<color=#00ccff>━━━ {charName}{adminTag} ━━━</color>");
        ctx.Reply($"SteamID: <color=#888888>{steamId}</color>  Статус: {status}");

        var ban = BanDatabase.GetActiveBan(steamId);
        if (ban != null)
            ctx.Reply($"<color=#ff4444>БАН</color> до {FormatExpiry(ban.ExpiresAt)} — {ban.Reason} (от {ban.BannedBy})");

        var mute = MuteDatabase.GetActiveMute(steamId);
        if (mute != null)
            ctx.Reply($"<color=#ff8800>МУТ</color> до {FormatExpiry(mute.ExpiresAt)} — {mute.Reason} (от {mute.MutedBy})");

        var warnCount = WarnDatabase.GetWarnCount(steamId);
        var warnColor = warnCount >= WarnDatabase.AutoBanThreshold ? "#ff4444" : warnCount > 0 ? "#ffaa00" : "#44ff44";
        ctx.Reply($"Предупреждения: <color={warnColor}>{warnCount}/{WarnDatabase.AutoBanThreshold}</color>");

        if (ban == null && mute == null && warnCount == 0)
            ctx.Reply("<color=#44ff44>Нарушений не зафиксировано.</color>");
    }

    // ── .history ──────────────────────────────────────────────────────────────
    [Command("history", description: "Moderation history: .history <player|SteamID>")]
    public void History(ChatCommandContext ctx, string playerName = "")
    {
        if (!IsAdmin(ctx)) return;
        if (string.IsNullOrWhiteSpace(playerName)) { ctx.Reply("Использование: .history <игрок|SteamID>"); return; }

        var world = ModerationHelpers.GetServerWorld();
        if (world == null) { ctx.Reply("Server world not ready."); return; }
        var em = world.EntityManager;

        bool isOnline = ModerationHelpers.TryFindUser(em, playerName, out _, out var user);
        if (!isOnline && !ModerationHelpers.TryFindUserOffline(em, playerName, out _, out user))
        { ctx.Reply($"<color=#ffaa00>Игрок '{playerName}' не найден.</color>"); return; }

        var charName = user.CharacterName.Value;
        var steamId  = user.PlatformId.ToString();
        var entries  = ModlogDatabase.GetForPlayer(steamId);

        ctx.Reply($"<color=#00ccff>━━━ История: {charName} ({steamId}) ━━━</color>");
        if (entries.Count == 0) { ctx.Reply("<color=#44ff44>Записей не найдено.</color>"); return; }

        foreach (var e in entries)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(e.At).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM HH:mm");
            var actionColor = e.Action switch
            {
                "ban"   or "kick"   => "#ff4444",
                "mute"  or "warn"   => "#ffaa00",
                "unban" or "unmute" or "clearwarns" => "#44ff44",
                _ => "#888888"
            };
            var exp = e.ExpiresAt.HasValue ? $" [{FormatRemaining(e.ExpiresAt)}]" : "";
            ctx.Reply($"<color={actionColor}>[{e.Action.ToUpper()}]</color> <color=#888888>{dt}</color> от {e.By}{exp} — {e.Reason}");
        }
    }

    // ── .banlist ──────────────────────────────────────────────────────────────
    [Command("banlist", description: "Show active bans")]
    public void BanList(ChatCommandContext ctx)
    {
        if (!IsAdmin(ctx)) return;
        var bans = BanDatabase.GetAll();
        if (bans.Count == 0) { ctx.Reply("<color=#44ff44>Нет активных банов.</color>"); return; }
        ctx.Reply($"<color=#ff4444>━━━ Активные баны ({bans.Count}) ━━━</color>");
        foreach (var b in bans)
            ctx.Reply($"<color=#ffcc00>{b.Name}</color> <color=#888888>({b.SteamId})</color> — {FormatExpiry(b.ExpiresAt)} — {b.Reason}");
    }

    // ── .mutelist ─────────────────────────────────────────────────────────────
    [Command("mutelist", description: "Show active mutes")]
    public void MuteList(ChatCommandContext ctx)
    {
        if (!IsAdmin(ctx)) return;
        var mutes = MuteDatabase.GetAll();
        if (mutes.Count == 0) { ctx.Reply("<color=#44ff44>Нет активных мутов.</color>"); return; }
        ctx.Reply($"<color=#ff8800>━━━ Активные муты ({mutes.Count}) ━━━</color>");
        foreach (var m in mutes)
            ctx.Reply($"<color=#ffcc00>{m.Name}</color> <color=#888888>({m.SteamId})</color> — {FormatExpiry(m.ExpiresAt)} — {m.Reason}");
    }

    // ── .warnlist ─────────────────────────────────────────────────────────────
    [Command("warnlist", description: "Show warnings: .warnlist [player]")]
    public void WarnList(ChatCommandContext ctx, string playerName = "")
    {
        if (!IsAdmin(ctx)) return;
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

            bool found = ModerationHelpers.TryFindUser(em, playerName, out _, out var u)
                      || ModerationHelpers.TryFindUserOffline(em, playerName, out _, out u);
            warns = found
                ? WarnDatabase.GetWarnsForPlayer(u.PlatformId.ToString())
                : WarnDatabase.GetAll().Where(w => w.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (warns.Count == 0) { ctx.Reply("<color=#44ff44>Нет предупреждений.</color>"); return; }
        ctx.Reply($"<color=#ffaa00>━━━ Предупреждения ({warns.Count}) ━━━</color>");
        foreach (var w in warns)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(w.WarnedAt).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM HH:mm");
            ctx.Reply($"<color=#ffcc00>{w.Name}</color> <color=#888888>[{dt}]</color> от <color=#00ccff>{w.WarnedBy}</color> — {w.Reason}");
        }
    }

    // ── .js-help ──────────────────────────────────────────────────────────────
    [Command("js-help", description: "Show all JSMonitor admin commands")]
    public void JsHelp(ChatCommandContext ctx)
    {
        if (!IsAdmin(ctx)) return;

        ctx.Reply("<color=#00ccff>━━━━━━━━━ JSMonitor команды ━━━━━━━━━</color>");

        ctx.Reply("<color=#ffcc00>[Баны]</color>");
        ctx.Reply("<color=#ffffff>.ban</color> <color=#888888><игрок|SteamID> <длит.> [причина]</color>  30m/2h/7d/0=навсегда");
        ctx.Reply("<color=#ffffff>.unban</color> <color=#888888><игрок|SteamID></color>  — разбанить (офлайн тоже)");
        ctx.Reply("<color=#ffffff>.banlist</color>  — активные баны с остатком времени");

        ctx.Reply("<color=#ffcc00>[Муты]</color>");
        ctx.Reply("<color=#ffffff>.mute</color> <color=#888888><игрок> <длит.> [причина]</color>");
        ctx.Reply("<color=#ffffff>.unmute</color> <color=#888888><игрок|SteamID></color>  — работает офлайн");
        ctx.Reply("<color=#ffffff>.mutelist</color>  — активные муты");

        ctx.Reply("<color=#ffcc00>[Предупреждения]</color>");
        ctx.Reply($"<color=#ffffff>.warn</color> <color=#888888><игрок> [причина]</color>  — {WarnDatabase.AutoBanThreshold} предупреждения = автобан");
        ctx.Reply("<color=#ffffff>.clearwarns</color> <color=#888888><игрок|SteamID></color>");
        ctx.Reply("<color=#ffffff>.warnlist</color> <color=#888888>[игрок]</color>  — все или по игроку");

        ctx.Reply("<color=#ffcc00>[Игроки]</color>");
        ctx.Reply("<color=#ffffff>.kick</color> <color=#888888><игрок> [причина]</color>  — частичное имя поддерживается");
        ctx.Reply("<color=#ffffff>.online</color>  — список онлайн со SteamID");
        ctx.Reply("<color=#ffffff>.admins</color>  — список администраторов онлайн");
        ctx.Reply("<color=#ffffff>.info</color> <color=#888888><игрок|SteamID></color>  — статус, баны, муты, варны");
        ctx.Reply("<color=#ffffff>.history</color> <color=#888888><игрок|SteamID></color>  — история нарушений");

        ctx.Reply("<color=#ffcc00>[Прочее]</color>");
        ctx.Reply("<color=#ffffff>.a</color> <color=#888888><текст...></color>  — чат только для администраторов");
        ctx.Reply("<color=#ffffff>.autoadmin</color> <color=#888888>list|add|remove <игрок|SteamID></color>  — авто-вход для администраторов");
        ctx.Reply("<color=#ffffff>.announce</color> <color=#888888><текст...></color>  — объявление всем (до 10 слов)");
        ctx.Reply("<color=#ffffff>.chatfilter</color> <color=#888888>list|add|remove <слово></color>  — фильтр чата");
        ctx.Reply("<color=#ffffff>.js-help</color>  — эта справка");

        ctx.Reply("<color=#00ccff>━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━</color>");
    }

    // ── .autoadmin ────────────────────────────────────────────────────────────
    [Command("autoadmin", description: "Auto-admin list: .autoadmin add|remove|list <player|SteamID>")]
    public void AutoAdmin(ChatCommandContext ctx, string action = "", string target = "")
    {
        if (!IsAdmin(ctx)) return;
        var by = ctx.Event.User.CharacterName.Value;

        switch (action.ToLowerInvariant())
        {
            case "list":
                var all = AdminDatabase.GetAll();
                if (all.Count == 0) { ctx.Reply("<color=#44ff44>Список авто-админов пуст.</color>"); return; }
                ctx.Reply($"<color=#ff5555>━━━ Авто-администраторы ({all.Count}) ━━━</color>");
                foreach (var e in all)
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(e.AddedAt).ToOffset(TimeSpan.FromHours(3)).ToString("dd.MM.yyyy");
                    var nameTag = string.IsNullOrEmpty(e.Name) ? "" : $" <color=#ffcc00>{e.Name}</color>";
                    ctx.Reply($"<color=#888888>{e.SteamId}</color>{nameTag} — добавлен <color=#00ccff>{e.AddedBy}</color> {dt}");
                }
                break;

            case "add":
            {
                if (string.IsNullOrWhiteSpace(target)) { ctx.Reply("Использование: .autoadmin add <игрок|SteamID>"); return; }

                var world = ModerationHelpers.GetServerWorld();
                if (world == null) { ctx.Reply("Server world not ready."); return; }
                var em = world.EntityManager;

                string steamId, charName;

                // SteamID passed directly
                if (target.Length == 17 && target.All(char.IsDigit))
                {
                    steamId = target;
                    // Try to resolve name from ECS (online or offline)
                    charName = "";
                    if (ModerationHelpers.TryFindUser(em, target, out _, out var u1) ||
                        ModerationHelpers.TryFindUserOffline(em, target, out _, out u1))
                        charName = u1.CharacterName.Value;
                }
                else
                {
                    // Name — look up online first, then offline
                    if (!ModerationHelpers.TryFindUser(em, target, out _, out var u2) &&
                        !ModerationHelpers.TryFindUserOffline(em, target, out _, out u2))
                    { ctx.Reply($"<color=#ffaa00>Игрок '{target}' не найден. Для оффлайн-игроков используйте SteamID.</color>"); return; }
                    steamId  = u2.PlatformId.ToString();
                    charName = u2.CharacterName.Value;
                }

                if (AdminDatabase.Add(steamId, charName, by))
                {
                    var label = string.IsNullOrEmpty(charName) ? steamId : $"{charName} ({steamId})";
                    ctx.Reply($"<color=#44ff44>{label} добавлен в авто-админы.</color>");
                }
                else
                    ctx.Reply($"<color=#ffaa00>SteamID {steamId} уже в списке.</color>");
                break;
            }

            case "remove":
            {
                if (string.IsNullOrWhiteSpace(target)) { ctx.Reply("Использование: .autoadmin remove <игрок|SteamID>"); return; }

                var entry = AdminDatabase.RemoveByNameOrSteamId(target);
                if (entry != null)
                {
                    var label = string.IsNullOrEmpty(entry.Name) ? entry.SteamId : $"{entry.Name} ({entry.SteamId})";
                    ctx.Reply($"<color=#44ff44>{label} удалён из авто-админов.</color>");
                }
                else
                    ctx.Reply($"<color=#ffaa00>'{target}' не найден в списке авто-админов.</color>");
                break;
            }

            default:
                ctx.Reply("Использование: .autoadmin list | .autoadmin add <игрок|SteamID> | .autoadmin remove <игрок|SteamID>");
                break;
        }
    }

    // ── .chatfilter ───────────────────────────────────────────────────────────
    [Command("chatfilter", description: "Chat filter: .chatfilter list|add|remove <word>")]
    public void ChatFilterCmd(ChatCommandContext ctx, string action = "", string word = "")
    {
        if (!IsAdmin(ctx)) return;
        switch (action.ToLowerInvariant())
        {
            case "list":
                var words = ChatFilter.GetWords();
                if (words.Count == 0) { ctx.Reply("<color=#44ff44>Фильтр пуст.</color>"); return; }
                ctx.Reply($"<color=#ffaa00>━━━ Фильтр ({words.Count} слов) ━━━</color>");
                ctx.Reply(string.Join(", ", words));
                break;

            case "add":
                if (string.IsNullOrWhiteSpace(word)) { ctx.Reply("Использование: .chatfilter add <слово>"); return; }
                if (ChatFilter.AddWord(word)) ctx.Reply($"<color=#44ff44>Слово '{word}' добавлено в фильтр.</color>");
                else ctx.Reply($"<color=#ffaa00>Слово '{word}' уже в фильтре.</color>");
                break;

            case "remove":
                if (string.IsNullOrWhiteSpace(word)) { ctx.Reply("Использование: .chatfilter remove <слово>"); return; }
                if (ChatFilter.RemoveWord(word)) ctx.Reply($"<color=#44ff44>Слово '{word}' удалено из фильтра.</color>");
                else ctx.Reply($"<color=#ffaa00>Слово '{word}' не найдено в фильтре.</color>");
                break;

            default:
                ctx.Reply("Использование: .chatfilter list | .chatfilter add <слово> | .chatfilter remove <слово>");
                break;
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
                // Find ConnectionStatusChangeReason in the same assembly as ServerBootstrapSystem
                // (avoids IL2CPP interop namespace mangling with full string lookup)
                Type? cscrType = typeof(ServerBootstrapSystem).Assembly
                    .GetTypes()
                    .FirstOrDefault(t => t.IsEnum && t.Name == "ConnectionStatusChangeReason");

                // Fallback: scan all assemblies by name only
                if (cscrType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            cscrType = asm.GetTypes()
                                .FirstOrDefault(t => t.IsEnum && t.Name == "ConnectionStatusChangeReason");
                            if (cscrType != null) break;
                        }
                        catch { }
                    }
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

    /// <summary>
    /// Grants admin rights to a connected player via AdminAuthSystem, then falls back
    /// to directly setting User.IsAdmin = true on the ECS component.
    /// </summary>
    public static bool GrantAdminRights(EntityManager em, Entity userEntity, ulong platformId)
    {
        try
        {
            var world = GetServerWorld();
            if (world == null) return false;

            // Approach 1: call ServerBootstrapSystem.AddAdmin(ulong) via reflection
            var sbs = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (sbs != null)
            {
                var bindAll = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var addAdminMethod = sbs.GetType().GetMethod("AddAdmin", bindAll, null, new[] { typeof(ulong) }, null);
                if (addAdminMethod != null)
                {
                    addAdminMethod.Invoke(sbs, new object[] { platformId });
                    Plugin.Logger.LogInfo($"[JSMonitor] GrantAdminRights: ServerBootstrapSystem.AddAdmin({platformId}) invoked.");
                    return true;
                }

                // Log available methods to help diagnose if AddAdmin signature differs
                foreach (var m in sbs.GetType().GetMethods(bindAll))
                    if (m.Name.IndexOf("Admin", StringComparison.OrdinalIgnoreCase) >= 0)
                        Plugin.Logger.LogInfo($"[JSMonitor] SBS admin method: {m.Name}({string.Join(", ", System.Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"))})");

                Plugin.Logger.LogWarning("[JSMonitor] GrantAdminRights: AddAdmin not found, falling back to SetComponentData.");
            }

            // Approach 2: directly set IsAdmin on the User component
            var user = em.GetComponentData<User>(userEntity);
            user.IsAdmin = true;
            em.SetComponentData(userEntity, user);
            Plugin.Logger.LogInfo($"[JSMonitor] GrantAdminRights: set User.IsAdmin=true for {platformId} via SetComponentData.");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[JSMonitor] GrantAdminRights error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends a message to all currently connected admins.
    /// Returns the number of recipients.
    /// </summary>
    public static int SendMessageToAdmins(EntityManager em, string text)
    {
        int count = 0;
        var qb = new EntityQueryBuilder(Allocator.Temp);
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
                    if (!u.IsConnected || !u.IsAdmin) continue;
                    SendMessageToUser(em, e, text);
                    count++;
                }
                catch { }
            }
            entities.Dispose();
        }
        finally { query.Dispose(); qb.Dispose(); }
        return count;
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
