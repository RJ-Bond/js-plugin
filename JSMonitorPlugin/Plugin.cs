using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppSystem.Collections;
using System.Collections;
using UnityEngine;

namespace JSMonitorPlugin;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    public static ManualLogSource Logger { get; private set; } = null!;

    // ── BepInEx config ────────────────────────────────────────────────────
    public static ConfigEntry<string> JSMonitorUrl    { get; private set; } = null!;
    public static ConfigEntry<string> ApiKey          { get; private set; } = null!;
    public static ConfigEntry<int>    ServerId        { get; private set; } = null!;
    public static ConfigEntry<int>    UpdateInterval  { get; private set; } = null!;

    private Harmony?       _harmony;
    private MapPushCoroutine? _coroutine;

    public override void Load()
    {
        Logger = Log;

        JSMonitorUrl   = Config.Bind("General", "JSMonitorUrl",   "",  "Full URL of your JSMonitor instance (e.g. https://example.com)");
        ApiKey         = Config.Bind("General", "ApiKey",         "",  "API token from your JSMonitor profile (Settings → API Token)");
        ServerId       = Config.Bind("General", "ServerId",       0,   "Server ID in JSMonitor (visible in the admin panel)");
        UpdateInterval = Config.Bind("General", "UpdateInterval", 60,  "How often to push map data, in seconds (min 10)");

        if (string.IsNullOrWhiteSpace(JSMonitorUrl.Value) || string.IsNullOrWhiteSpace(ApiKey.Value) || ServerId.Value <= 0)
        {
            Logger.LogWarning("[JSMonitor] Not configured — edit BepInEx/config/JSMonitorPlugin.cfg and restart.");
            return;
        }

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll();

        // Start push coroutine via BepInEx IL2CPP coroutine helper
        _coroutine = AddComponent<MapPushCoroutine>();

        Logger.LogInfo($"[JSMonitor] Loaded. Pushing to {JSMonitorUrl.Value} every {UpdateInterval.Value}s for server #{ServerId.Value}");
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        if (_coroutine != null)
            GameObject.Destroy(_coroutine);
        return true;
    }
}
