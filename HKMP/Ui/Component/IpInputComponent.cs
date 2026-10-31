using System.Collections.Generic;
using Hkmp.Ui.Resources;
using UnityEngine;

namespace Hkmp.Ui.Component;

/// <summary>
/// Input component specifically for the IP input.
/// </summary>
internal class IpInputComponent : HiddenInputComponent {
    /// <summary>
    /// List of characters that cannot be input in this field.
    /// </summary>
    private static readonly List<char> BlacklistedChars = new List<char> {
        ' ',
        '\n',
        '\t',
        '\v',
        '\f',
        '\b',
        '\r'
    };

    public IpInputComponent(
        ComponentGroup componentGroup,
        Vector2 position,
        string defaultValue,
        string placeholderText
    ) : base(
        componentGroup,
        position,
        defaultValue,
        placeholderText,
        UiManager.NormalFontSize
    ) {
        InputField.onValidateInput += Validate;
    }

    public IpInputComponent(
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
        fontSize
    ) {
        InputField.onValidateInput += Validate;
    }

    private static char Validate(string text, int index, char addedChar) =>
        BlacklistedChars.Contains(addedChar) ? '\0' : addedChar;
}
