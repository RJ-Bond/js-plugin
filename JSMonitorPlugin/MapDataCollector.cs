using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using System;
using System.Collections.Generic;
using System.Text;

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

    /// <summary>
    /// Dumps ECS component types of territory entities to the log.
    /// Compares a large territory (likely buildable) vs a small one (likely NPC/road).
    /// Set Debug.DumpOnNextPush = true in JSMonitorPlugin.cfg to trigger.
    /// </summary>
    public static void DumpTerritoryComponents()
    {
        var world = ServerWorld;
        if (world == null) { Plugin.Logger.LogWarning("[JSMonitor][Dump] Server world not ready."); return; }
        var em = world.EntityManager;

        var qb    = new EntityQueryBuilder(Allocator.Temp);
        qb.AddAll(ComponentType.ReadOnly<CastleTerritory>());
        var query = qb.Build(em);
        var entities = query.ToEntityArray(Allocator.Temp);

        Plugin.Logger.LogInfo($"[JSMonitor][Dump] Total CastleTerritory entities: {entities.Length}");

        // Find largest (most blocks) and smallest (fewest blocks, min 5) territory.
        int maxBlocks = 0, minBlocks = int.MaxValue;
        int maxIdx = -1, minIdx = -1;
        for (int i = 0; i < entities.Length; i++)
        {
            try
            {
                if (!em.HasBuffer<CastleTerritoryBlocks>(entities[i])) continue;
                int len = em.GetBuffer<CastleTerritoryBlocks>(entities[i]).Length;
                if (len > maxBlocks) { maxBlocks = len; maxIdx = i; }
                if (len >= 5 && len < minBlocks) { minBlocks = len; minIdx = i; }
            }
            catch { }
        }

        void DumpEntity(int idx, string label)
        {
            if (idx < 0) return;
            var e = entities[idx];
            try
            {
                int blockCount = em.HasBuffer<CastleTerritoryBlocks>(e)
                    ? em.GetBuffer<CastleTerritoryBlocks>(e).Length : 0;

                var types = em.GetComponentTypes(e, Allocator.Temp);
                var sb = new StringBuilder();
                foreach (var ct in types)
                {
                    var mt = TypeManager.GetType(ct.TypeIndex);
                    sb.Append(mt != null ? mt.Name : ct.TypeIndex.ToString()).Append(" | ");
                }
                types.Dispose();

                Plugin.Logger.LogInfo($"[JSMonitor][Dump] === {label} (blocks={blockCount}) ===");
                // Split into 3 lines to avoid log truncation
                string all = sb.ToString();
                int chunk = all.Length / 3;
                Plugin.Logger.LogInfo($"[JSMonitor][Dump]   {all.Substring(0, Math.Min(chunk, all.Length))}");
                if (all.Length > chunk)
                    Plugin.Logger.LogInfo($"[JSMonitor][Dump]   {all.Substring(chunk, Math.Min(chunk, all.Length - chunk))}");
                if (all.Length > chunk * 2)
                    Plugin.Logger.LogInfo($"[JSMonitor][Dump]   {all.Substring(chunk * 2)}");
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"[JSMonitor][Dump] Error dumping {label}: {ex.Message}"); }
        }

        DumpEntity(maxIdx, "LARGE (buildable?)");
        DumpEntity(minIdx, "SMALL (NPC/road?)");

        entities.Dispose();
        query.Dispose();
        qb.Dispose();
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
            FreePlots = CollectFreePlots(em),
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

    // ── Free plots ────────────────────────────────────────────────────────

    static List<FreePlotEntry> CollectFreePlots(EntityManager em)
    {
        var result = new List<FreePlotEntry>();

        // Step 1: collect territory entities that are already claimed.
        // CastleHeart.CastleTerritoryEntity points to the territory entity it occupies.
        var claimedEntities = new HashSet<Entity>();
        {
            var qb = new EntityQueryBuilder(Allocator.Temp);
            qb.AddAll(ComponentType.ReadOnly<CastleHeart>());
            var q = qb.Build(em);
            try
            {
                var es = q.ToEntityArray(Allocator.Temp);
                foreach (var e in es)
                {
                    try
                    {
                        var heart = em.GetComponentData<CastleHeart>(e);
                        claimedEntities.Add(heart.CastleTerritoryEntity);
                    }
                    catch { }
                }
                es.Dispose();
            }
            finally { q.Dispose(); qb.Dispose(); }
        }

        float xMin = Plugin.WorldXMin.Value;
        float xMax = Plugin.WorldXMax.Value;
        float zMin = Plugin.WorldZMin.Value;
        float zMax = Plugin.WorldZMax.Value;

        float scale = Plugin.BlockWorldSize.Value;

        // Diagnostic: log CastleHeart world pos vs territory centroid to validate formula.
        // Only runs once (first claimed territory found).
        bool diagLogged = false;

        // Step 2: iterate all territory entities, skip claimed, compute world centroid.
        var queryBuilder = new EntityQueryBuilder(Allocator.Temp);
        queryBuilder.AddAll(ComponentType.ReadOnly<CastleTerritory>());
        var query = queryBuilder.Build(em);

        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var entity in entities)
            {
                try
                {
                    bool isClaimed = claimedEntities.Contains(entity);

                    if (!em.HasBuffer<CastleTerritoryBlocks>(entity)) continue;
                    var blocks = em.GetBuffer<CastleTerritoryBlocks>(entity);
                    if (blocks.Length == 0) continue;

                    float sumX = 0f, sumZ = 0f;
                    for (int i = 0; i < blocks.Length; i++)
                    {
                        sumX += blocks[i].BlockCoordinate.x * scale + xMin;
                        sumZ += zMax - blocks[i].BlockCoordinate.y * scale;
                    }
                    float cx = sumX / blocks.Length;
                    float cz = sumZ / blocks.Length;

                    // Diagnostic: for first claimed territory, compare computed centroid
                    // with the actual CastleHeart world position.
                    if (!diagLogged && isClaimed)
                    {
                        diagLogged = true;
                        try
                        {
                            var ct = em.GetComponentData<CastleTerritory>(entity);
                            if (em.Exists(ct.CastleHeart) && em.HasComponent<LocalToWorld>(ct.CastleHeart))
                            {
                                var heartPos = em.GetComponentData<LocalToWorld>(ct.CastleHeart).Position;
                                Plugin.Logger.LogInfo($"[JSMonitor][FreePlot] Diag: territory centroid=({cx:F0},{cz:F0}) heartPos=({heartPos.x:F0},{heartPos.z:F0}) blocks={blocks.Length}");
                            }
                        }
                        catch { }
                    }

                    if (isClaimed) continue;

                    // Bounds filter — skip territories outside visible map area.
                    if (cx < xMin || cx > xMax || cz < zMin || cz > zMax) continue;

                    result.Add(new FreePlotEntry { X = cx, Z = cz });
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
