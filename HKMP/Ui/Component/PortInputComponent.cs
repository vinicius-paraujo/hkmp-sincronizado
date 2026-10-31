using System.Collections.Generic;
using Hkmp.Ui.Resources;
using UnityEngine;

namespace Hkmp.Ui.Component;

/// <summary>
/// Input component specifically for the port input.
/// </summary>
internal class PortInputComponent : InputComponent {
    /// <summary>
    /// List of characters that are allowed to be input.
    /// </summary>
    private static readonly List<char> AllowedChars = new List<char> {
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'
    };

    public PortInputComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        string defaultValue,
        string placeholderText
    ) : base(
        componentGroup,
        position,
        defaultValue,
        placeholderText,
        characterLimit: 5
    ) {
        InputField.onValidateInput += Validate;
    }

    public PortInputComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        Vector2 size,
        string defaultValue,
        string placeholderText,
        MultiStateSprite bgSprite,
        int fontSize
    ) : base(
        componentGroup,
        position,
        size,
        defaultValue,
        placeholderText,
        bgSprite,
        FontManager.UIFontRegular,
        fontSize,
        characterLimit: 5
    ) {
        InputField.onValidateInput += Validate;
    }

    private static char Validate(string text, int index, char addedChar) =>
        AllowedChars.Contains(addedChar) ? addedChar : '\0';
}
