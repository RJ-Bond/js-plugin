using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppSystem.Collections;
using System.Collections;
using System.Reflection;
using UnityEngine;
using VampireCommandFramework;

namespace JSMonitorPlugin;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("gg.deca.VampireCommandFramework")]
public class Plugin : BasePlugin
{
    public static Plugin          Instance { get; private set; } = null!;
    public static ManualLogSource Logger   { get; private set; } = null!;

    // ── BepInEx config ────────────────────────────────────────────────────
    public static ConfigEntry<string> JSMonitorUrl      { get; private set; } = null!;
    public static ConfigEntry<string> ApiKey            { get; private set; } = null!;
    public static ConfigEntry<int>    ServerId          { get; private set; } = null!;
    public static ConfigEntry<int>    UpdateInterval    { get; private set; } = null!;
    public static ConfigEntry<string> DiscordWebhookUrl { get; private set; } = null!;
    // World bounds — used to calculate and filter free territory positions.
    public static ConfigEntry<float> WorldXMin      { get; private set; } = null!;
    public static ConfigEntry<float> WorldXMax      { get; private set; } = null!;
    public static ConfigEntry<float> WorldZMin      { get; private set; } = null!;
    public static ConfigEntry<float> WorldZMax      { get; private set; } = null!;
    public static ConfigEntry<float> BlockWorldSize { get; private set; } = null!;
    // Block tile coordinate origin in world space (derived from game engine tile grid).
    public static ConfigEntry<float> BlockXOrigin   { get; private set; } = null!;
    public static ConfigEntry<float> BlockZOrigin   { get; private set; } = null!;
    // Diagnostic: dump component types of territory entities on next push (auto-resets).
    public static ConfigEntry<bool>  DumpOnNextPush { get; private set; } = null!;
    // Auto announcer interval (0 = disabled)
    public static ConfigEntry<int>   AnnouncerInterval { get; private set; } = null!;

    private Harmony?            _harmony;
    private MapPushCoroutine?   _coroutine;
    private EventPushCoroutine? _eventCoroutine;
    private AutoAnnouncer?      _announcer;

    public override void Load()
    {
        Instance = this;
        Logger   = Log;

        JSMonitorUrl      = Config.Bind("General", "JSMonitorUrl",      "",    "Full URL of your JSMonitor instance (e.g. https://example.com)");
        ApiKey            = Config.Bind("General", "ApiKey",            "",    "API token from your JSMonitor profile (Settings → API Token)");
        ServerId          = Config.Bind("General", "ServerId",          0,     "Server ID in JSMonitor (visible in the admin panel)");
        UpdateInterval    = Config.Bind("General", "UpdateInterval",    60,    "How often to push map data, in seconds (min 10)");
        DiscordWebhookUrl = Config.Bind("Discord", "WebhookUrl",        "",     "Discord webhook URL for chat and connection events (leave empty to disable)");
        WorldXMin         = Config.Bind("Map", "WorldXMin",         -2880f,  "Western world boundary.");
        WorldXMax         = Config.Bind("Map", "WorldXMax",           160f,  "Eastern world boundary.");
        WorldZMin         = Config.Bind("Map", "WorldZMin",         -1700f,  "Southern world boundary. Default -1700 excludes far-south territories outside the visible map.");
        WorldZMax         = Config.Bind("Map", "WorldZMax",           640f,  "Northern world boundary.");
        BlockWorldSize    = Config.Bind("Map", "BlockWorldSize",        5f,     "Tile size in world units.");
        BlockXOrigin      = Config.Bind("Map", "BlockXOrigin",      -3221f,  "World X at block tile X=0. Do not change unless game updates.");
        BlockZOrigin      = Config.Bind("Map", "BlockZOrigin",       -221f,  "World Z at block tile Y=0. Do not change unless game updates.");
        DumpOnNextPush    = Config.Bind("Debug", "DumpOnNextPush",   false,  "Set true to dump ECS component types of territory entities on next push (auto-resets).");
        AnnouncerInterval = Config.Bind("Announcer", "IntervalSeconds", 300,   "Auto-announcer interval in seconds (0 = disabled). Messages are loaded from JSMonitorPlugin_announcements.txt.");

        BanDatabase.Load();
        MuteDatabase.Load();
        WarnDatabase.Load();
        ChatFilter.Load();

        if (string.IsNullOrWhiteSpace(JSMonitorUrl.Value) || string.IsNullOrWhiteSpace(ApiKey.Value) || ServerId.Value <= 0)
        {
            Logger.LogWarning("[JSMonitor] Not configured — edit BepInEx/config/JSMonitorPlugin.cfg and restart.");
            return;
        }

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll();

        CommandRegistry.RegisterAll(Assembly.GetExecutingAssembly());

        // Start coroutines
        _coroutine      = AddComponent<MapPushCoroutine>();
        _eventCoroutine = AddComponent<EventPushCoroutine>();
        _announcer      = AddComponent<AutoAnnouncer>();

        Logger.LogInfo($"[JSMonitor] Loaded. Pushing to {JSMonitorUrl.Value} every {UpdateInterval.Value}s for server #{ServerId.Value}");
    }

    public override bool Unload()
    {
        CommandRegistry.UnregisterAssembly(Assembly.GetExecutingAssembly());
        _harmony?.UnpatchSelf();
        if (_coroutine != null)
            GameObject.Destroy(_coroutine);
        if (_eventCoroutine != null)
            GameObject.Destroy(_eventCoroutine);
        if (_announcer != null)
            GameObject.Destroy(_announcer);
        return true;
    }
}
