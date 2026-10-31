using TMPro;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui.Resources;

/// <summary>
/// The font manager that stores fonts that are used in-game.
/// </summary>
internal static class FontManager {
    /// <summary>
    /// The font used for UI.
    /// </summary>
    public static Font UIFontRegular;

    /// <summary>
    /// The font of the game's menu titles and buttons.
    /// </summary>
    public static Font UITitleFont;

    /// <summary>
    /// The font used for usernames above player objects.
    /// </summary>
    public static TMP_FontAsset InGameNameFont;

    /// <summary>
    /// Load the fonts by trying to find them in the game through Unity.
    /// </summary>
    public static void LoadFonts() {
        foreach (var font in UnityEngine.Resources.FindObjectsOfTypeAll<Font>()) {
            switch (font.name) {
                case "Perpetua":
                    UIFontRegular = font;
                    break;
                case "TrajanPro-Bold":
                    UITitleFont = font;
                    break;
            }
        }

        foreach (var textMeshProFont in UnityEngine.Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) {
            switch (textMeshProFont.name) {
                case "TrajanPro-Bold SDF":
                    InGameNameFont = textMeshProFont;
                    break;
            }
        }

        if (UITitleFont == null) {
            UITitleFont = UIFontRegular;
        }

        if (UIFontRegular == null) {
            Logger.Error("UI font regular is missing!");
        }

        if (InGameNameFont == null) {
            Logger.Error("In-game name font is missing!");
        }
    }
}
