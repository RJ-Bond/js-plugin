using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JSMonitorPlugin;

/// <summary>
/// Loads a list of banned words from JSMonitorPlugin_filter.txt (one word per line).
/// Used by ChatHooks to auto-warn or block messages containing banned words.
/// </summary>
public static class ChatFilter
{
    static readonly string _path = Path.Combine(
        BepInEx.Paths.ConfigPath, "JSMonitorPlugin_filter.txt");

    static readonly object _lock = new();
    static List<string> _words = [];

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    _words = File.ReadAllLines(_path)
                        .Select(l => l.Trim().ToLowerInvariant())
                        .Where(l => l.Length > 0 && !l.StartsWith('#'))
                        .ToList();
                }
                else
                {
                    // Create template file
                    File.WriteAllText(_path,
                        "# Chat filter — one banned word/phrase per line\n" +
                        "# Lines starting with # are comments\n");
                    _words = [];
                }
                Plugin.Logger.LogInfo($"[JSMonitor] ChatFilter: loaded {_words.Count} banned word(s)");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[JSMonitor] ChatFilter load error: {ex.Message}");
                _words = [];
            }
        }
    }

    /// <summary>
    /// Checks if the message contains any banned word.
    /// Returns the matched word or null.
    /// </summary>
    public static string? CheckMessage(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var lower = message.ToLowerInvariant();
        lock (_lock)
        {
            foreach (var word in _words)
                if (lower.Contains(word))
                    return word;
        }
        return null;
    }

    public static List<string> GetWords()
    {
        lock (_lock) { return [.. _words]; }
    }

    /// <summary>Adds a word to the filter. Returns false if it already exists.</summary>
    public static bool AddWord(string word)
    {
        word = word.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(word)) return false;
        lock (_lock)
        {
            if (_words.Contains(word)) return false;
            _words.Add(word);
            SaveWords();
            return true;
        }
    }

    /// <summary>Removes a word from the filter. Returns false if not found.</summary>
    public static bool RemoveWord(string word)
    {
        word = word.Trim().ToLowerInvariant();
        lock (_lock)
        {
            int removed = _words.RemoveAll(w => w == word);
            if (removed > 0) SaveWords();
            return removed > 0;
        }
    }

    static void SaveWords()
    {
        try
        {
            var lines = new List<string>
            {
                "# Chat filter — one banned word/phrase per line",
                "# Lines starting with # are comments"
            };
            lines.AddRange(_words);
            File.WriteAllLines(_path, lines);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogWarning($"[JSMonitor] ChatFilter save error: {ex.Message}");
        }
    }
}
