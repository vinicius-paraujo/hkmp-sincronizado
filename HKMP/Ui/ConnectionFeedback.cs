using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace Hkmp.Ui;

internal static class ConnectionFeedback {
    public static string NameInUse() {
        var language = Language.Language.CurrentLanguage() == Language.LanguageCode.PT ? "pt-BR" : "en";
        return Read(language)["nameInUse"];
    }

    private static Dictionary<string, string> Read(string language) {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Hkmp.Resource.connection-{language}.json");
        using var reader = new StreamReader(stream);
        return JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
    }
}
