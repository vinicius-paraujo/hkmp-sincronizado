using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace Hkmp.Ui;

/// <summary>
/// The player-facing text of the multiplayer menu, in the game's language with English as the fallback.
/// </summary>
internal static class ConnectionFeedback {
    private static readonly Dictionary<string, Dictionary<string, string>> Cache =
        new Dictionary<string, Dictionary<string, string>>();

    public static string Get(string key) {
        var language = Language.Language.CurrentLanguage() == Language.LanguageCode.PT ? "pt-BR" : "en";
        return Read(language).TryGetValue(key, out var text) ? text : Read("en")[key];
    }

    private static Dictionary<string, string> Read(string language) {
        if (Cache.TryGetValue(language, out var texts)) return texts;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Hkmp.Resource.connection-{language}.json");
        using var reader = new StreamReader(stream);
        return Cache[language] = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
    }
}
