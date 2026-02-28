using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using System;
using System.Collections.Generic;

namespace JSMonitorPlugin;

/// <summary>
/// Reads current castle and player data directly from the V Rising ECS world.
/// </summary>
public static class MapDataCollector
{
    static World? ServerWorld
    {
        get
        {
            if (World.s_AllWorlds == null) return null;
            foreach (var world in World.s_AllWorlds)
                if (world != null && world.Name == "Server") return world;
            return null;
        }
    }

    public static MapSnapshot? Collect()
    {
        var world = ServerWorld;
        if (world == null) return null;

        var em = world.EntityManager;

        var snapshot = new MapSnapshot
        {
            Players   = CollectPlayers(em),
            Castles   = CollectCastles(em, world),
            FreePlots = CollectFreePlots(em)
        };

        return snapshot;
    }

    // ── Players ───────────────────────────────────────────────────────────

    static List<PlayerEntry> CollectPlayers(EntityManager em)
    {
        var result = new List<PlayerEntry>();

        var queryBuilder = new EntityQueryBuilder(Allocator.Temp);
        queryBuilder.AddAll(ComponentType.ReadOnly<User>());
        var query = queryBuilder.Build(em);

        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                try
                {
                    var user = em.GetComponentData<User>(entity);

                    // Only online players
                    if (!user.IsConnected) continue;

                    // Get character entity for position
                    var charEntity = user.LocalCharacter.GetEntityOnServer();
                    if (!em.Exists(charEntity)) continue;

                    if (!em.HasComponent<LocalToWorld>(charEntity)) continue;
                    var ltw = em.GetComponentData<LocalToWorld>(charEntity);
                    var pos = ltw.Position;

                    // Clan name (optional)
                    var clanName = "";
                    if (em.HasComponent<ClanTeam>(entity))
                    {
                        var clan = em.GetComponentData<ClanTeam>(entity);
                        clanName = clan.Name.Value;
                    }

                    // Health (optional)
                    float health = 0f;
                    if (em.HasComponent<Health>(charEntity))
                    {
                        var h = em.GetComponentData<Health>(charEntity);
                        health = h.MaxHealth > 0 ? h.Value / h.MaxHealth : 0f;
                    }

                    result.Add(new PlayerEntry
                    {
                        Name    = user.CharacterName.Value,
                        Clan    = clanName,
                        X       = pos.x,
                        Z       = pos.z,
                        Health  = health,
                        IsAdmin = user.IsAdmin
                    });
                }
                catch { /* skip this entity */ }
            }
            entities.Dispose();
        }
        finally
        {
            query.Dispose();
            queryBuilder.Dispose();
        }

        return result;
    }

    // ── Castles ───────────────────────────────────────────────────────────

    static List<CastleEntry> CollectCastles(EntityManager em, World world)
    {
        var result = new List<CastleEntry>();

        var queryBuilder = new EntityQueryBuilder(Allocator.Temp);
        queryBuilder.AddAll(ComponentType.ReadOnly<CastleHeart>());
        queryBuilder.AddAll(ComponentType.ReadOnly<LocalToWorld>());
        var query = queryBuilder.Build(em);

        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                try
                {
                    var ltw   = em.GetComponentData<LocalToWorld>(entity);
                    var heart = em.GetComponentData<CastleHeart>(entity);
                    var pos   = ltw.Position;

                    // CastleHeart.Level is 0-based; +1 gives display tier (1–4)
                    int tier = (int)heart.Level + 1;

                    // Owner via UserOwner → User
                    var ownerName = "";
                    var clanName  = "";
                    if (em.HasComponent<UserOwner>(entity))
                    {
                        var uo = em.GetComponentData<UserOwner>(entity);
                        var ownerEntity = uo.Owner.GetEntityOnServer();
                        if (em.Exists(ownerEntity) && em.HasComponent<User>(ownerEntity))
                        {
                            var user = em.GetComponentData<User>(ownerEntity);
                            ownerName = user.CharacterName.Value;

                            if (em.HasComponent<ClanTeam>(ownerEntity))
                            {
                                var clan = em.GetComponentData<ClanTeam>(ownerEntity);
                                clanName = clan.Name.Value;
                            }
                        }
                    }

                    result.Add(new CastleEntry
                    {
                        Owner = ownerName,
                        Clan  = clanName,
                        X     = pos.x,
                        Z     = pos.z,
                        Tier  = tier
                    });
                }
                catch { /* skip */ }
            }
            entities.Dispose();
        }
        finally
        {
            query.Dispose();
            queryBuilder.Dispose();
        }

        return result;
    }

    // ── Free plots ────────────────────────────────────────────────────────────
    // CastleTerritory entities have no LocalToWorld; position sourcing is TBD.
    // Returns empty list for now — slot reserved for future implementation.

    static List<FreePlotEntry> CollectFreePlots(EntityManager em) => [];
}

// ── Data models ───────────────────────────────────────────────────────────────

public class MapSnapshot
{
    public List<PlayerEntry>   Players   { get; set; } = [];
    public List<CastleEntry>   Castles   { get; set; } = [];
    public List<FreePlotEntry> FreePlots { get; set; } = [];
}

public class PlayerEntry
{
    public string Name    { get; set; } = "";
    public string Clan    { get; set; } = "";
    public float  X       { get; set; }
    public float  Z       { get; set; }
    public float  Health  { get; set; }
    public bool   IsAdmin { get; set; }
}

public class CastleEntry
{
    public string Owner { get; set; } = "";
    public string Clan  { get; set; } = "";
    public float  X     { get; set; }
    public float  Z     { get; set; }
    public int    Tier  { get; set; }
}

public class FreePlotEntry
{
    public float X { get; set; }
    public float Z { get; set; }
}
