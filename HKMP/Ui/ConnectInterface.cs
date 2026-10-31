using System;
using System.Collections;
using System.Collections.Generic;
using Hkmp.Game.Server;
using Hkmp.Game.Settings;
using Hkmp.Networking;
using Hkmp.Networking.Client;
using Hkmp.Ui.Component;
using Hkmp.Ui.Resources;
using Hkmp.Util;
using UnityEngine;
using Logger = Hkmp.Logging.Logger;

namespace Hkmp.Ui;

/// <summary>
/// Class for creating and managing the connect interface.
/// </summary>
internal class ConnectInterface {
    /// <summary>
    /// The width of the form and the widths of the address and port fields on its row.
    /// </summary>
    private const float FormWidth = 440f;
    private const float PortWidth = 110f;
    private const float RowGap = 24f;

    private const float LabelHeight = 30f;
    private const int FieldFontSize = 28;
    private const int LabelFontSize = 20;
    private const int FeedbackFontSize = 20;
    private const float InputTextInset = 5f;

    /// <summary>
    /// The time in seconds to hide the feedback text after it appeared.
    /// </summary>
    private const float FeedbackTextHideTime = 10f;

    private static readonly Color Text = new Color32(0xED, 0xE5, 0xED, 0xFF);
    private static readonly Color Amber = new Color32(0xEB, 0xB2, 0x3C, 0xFF);
    private static readonly Color Muted = new Color32(0xED, 0xE5, 0xED, 0xB0);
    private static readonly Color Line = new Color32(0xED, 0xE5, 0xED, 0xD0);

    private readonly ModSettings _modSettings;
    private readonly ComponentGroup _connectGroup;

    private readonly List<(ITextComponent Text, string Key)> _labels = new List<(ITextComponent, string)>();

    private InputComponent _usernameInput;
    private InputComponent _addressInput;
    private InputComponent _portInput;

    private ITextComponent _addressLabel;
    private ITextComponent _portLabel;
    private float _portJoinX;
    private float _portHostX;
    private Vector2 _feedbackJoinPosition;
    private Vector2 _feedbackHostPosition;
    private ITextComponent _hostHint;
    private ButtonComponent _joinMode;
    private ButtonComponent _hostMode;
    private ImageComponent _joinUnderline;
    private ImageComponent _hostUnderline;

    /// <summary>
    /// The connect button, shown in the join mode.
    /// </summary>
    private IButtonComponent _connectionButton;

    /// <summary>
    /// The server host button, shown in the host mode.
    /// </summary>
    private IButtonComponent _serverButton;

    private ITextComponent _feedbackText;
    private Coroutine _feedbackHideCoroutine;
    private bool _connecting;

    /// <summary>
    /// Event that is executed when the connect button is pressed.
    /// </summary>
    public event Action<string, int, string> ConnectButtonPressed;

    /// <summary>
    /// Event that is executed when the start hosting button is pressed.
    /// </summary>
    public event Action<string, int> StartHostButtonPressed;

    public ConnectInterface(
        ModSettings modSettings,
        ComponentGroup connectGroup
    ) {
        _modSettings = modSettings;
        _connectGroup = connectGroup;

        Component.Component.CreateCentered = true;
        CreateConnectUi();
        Component.Component.CreateCentered = false;
        SetHostMode(false);
    }

    /// <summary>
    /// Applies the game's current language, which the player can change while the game runs.
    /// </summary>
    public void RefreshLanguage() {
        foreach (var (text, key) in _labels) {
            text.SetText(ConnectionFeedback.Get(key));
        }

        _joinMode.SetText(ConnectionFeedback.Get("join"));
        _hostMode.SetText(ConnectionFeedback.Get("host"));
        _serverButton.SetText(Action("open"));
        _connectionButton.SetText(Action(_connecting ? "connecting" : "connect"));
    }

    /// <summary>
    /// Callback method for when the client disconnects.
    /// </summary>
    public void OnClientDisconnect() {
        ResetConnectionButton();
        _connectionButton.SetOnPress(OnConnectButtonPressed);
    }

    /// <summary>
    /// Callback method for when the client successfully connects.
    /// </summary>
    public void OnSuccessfulConnect() {
        ShowFeedback(Text, "connected");
        ResetConnectionButton();
    }

    /// <summary>
    /// Callback method for when the client fails to connect.
    /// </summary>
    /// <param name="result">The result of the failed connection.</param>
    public void OnFailedConnect(ConnectionFailedResult result) {
        switch (result.Reason) {
            case ConnectionFailedReason.InvalidAddons:
                ShowFeedback(Amber, "versionMismatch");
                break;
            case ConnectionFailedReason.SocketException:
            case ConnectionFailedReason.IOException:
                ShowFeedback(Amber, "internalError");
                break;
            case ConnectionFailedReason.TimedOut:
                ShowFeedback(Amber, "noResponse", _addressInput, _portInput);
                break;
            case ConnectionFailedReason.Other:
                var message = ((ConnectionFailedMessageResult) result).Message;
                if (message == ConnectionRejection.UsernameInUse) {
                    ShowFeedback(Amber, "nameInUse", _usernameInput);
                } else {
                    // Addons can reject with any text, so it stays in the log and the player gets a translated line
                    Logger.Info($"Connection rejected: {message}");
                    ShowFeedback(Amber, "rejected");
                }

                break;
            default:
                ShowFeedback(Amber, "internalError");
                break;
        }

        ResetConnectionButton();
    }

    /// <summary>
    /// Create the connection UI.
    /// </summary>
    private void CreateConnectUi() {
        var x = 1920f / 2f;
        var y = 820f;
        var line = LineSprites.Field;

        // The menu has several backgrounds, some of them bright, so the form brings its own soft shadow
        // ReSharper disable once ObjectCreationAsStatement
        new ImageComponent(_connectGroup, new Vector2(x, 630f), new Vector2(1300f, 1000f), LineSprites.Backing);

        var title = new TextComponent(_connectGroup, new Vector2(x, 895f), new Vector2(FormWidth + 100f, 70f), "", 54);
        title.SetFont(FontManager.UITitleFont);
        title.SetColor(Text);
        _labels.Add((title, "title"));

        _joinMode = ModeButton(x - 90f, y);
        _hostMode = ModeButton(x + 90f, y);
        _joinMode.SetOnPress(() => SetHostMode(false));
        _hostMode.SetOnPress(() => SetHostMode(true));
        _joinUnderline = new ImageComponent(_connectGroup, new Vector2(x - 90f, y - 22f), new Vector2(120f, 2f), LineSprites.Accent);
        _hostUnderline = new ImageComponent(_connectGroup, new Vector2(x + 90f, y - 22f), new Vector2(120f, 2f), LineSprites.Accent);
        y -= 66f;

        Label(x, y, FormWidth, "name");
        y -= LabelHeight + 12f;
        _usernameInput = new InputComponent(
            _connectGroup,
            new Vector2(x, y),
            new Vector2(FormWidth, InputComponent.DefaultHeight),
            _modSettings.Username,
            "",
            line,
            FontManager.UIFontRegular,
            FieldFontSize,
            characterLimit: 20,
            onValidateInput: (_, _, addedChar) => char.IsLetterOrDigit(addedChar) ? addedChar : '\0'
        );
        Style(_usernameInput);
        y -= InputComponent.DefaultHeight + 22f;

        var addressWidth = FormWidth - PortWidth - RowGap;
        var addressX = x - FormWidth / 2f + addressWidth / 2f;
        var portX = x + FormWidth / 2f - PortWidth / 2f;
        _portJoinX = portX;
        _portHostX = x - FormWidth / 2f + PortWidth / 2f;

        _addressLabel = Label(addressX, y, addressWidth, "address");
        _portLabel = Label(portX, y, PortWidth, "port");
        y -= LabelHeight + 12f;

        _addressInput = new IpInputComponent(
            _connectGroup,
            new Vector2(addressX, y),
            new Vector2(addressWidth, InputComponent.DefaultHeight),
            _modSettings.ConnectAddress,
            "",
            line,
            FieldFontSize
        );
        Style(_addressInput);

        var joinPort = _modSettings.ConnectPort;
        _portInput = new PortInputComponent(
            _connectGroup,
            new Vector2(portX, y),
            new Vector2(PortWidth, InputComponent.DefaultHeight),
            joinPort == -1 ? "" : joinPort.ToString(),
            "",
            line,
            FieldFontSize
        );
        Style(_portInput);

        _hostHint = new TextComponent(
            _connectGroup,
            new Vector2(x, y - InputComponent.DefaultHeight - 4f),
            new Vector2(FormWidth, LabelHeight),
            "",
            LabelFontSize,
            FontStyle.Normal,
            TextAnchor.MiddleCenter
        );
        ((TextComponent) _hostHint).SetFont(FontManager.UITitleFont);
        _hostHint.SetColor(Text);
        _labels.Add((_hostHint, "hostHint"));
        y -= InputComponent.DefaultHeight + 30f;

        _connectionButton = ActionButton(x, y);
        _connectionButton.SetOnPress(OnConnectButtonPressed);
        // The host hint takes the line under the port, so this button and its feedback sit one line lower
        var hostOffset = LabelHeight + 14f;
        _serverButton = ActionButton(x, y - hostOffset);
        _serverButton.SetOnPress(OnStartButtonPressed);
        y -= ButtonComponent.DefaultHeight + 14f;

        _feedbackJoinPosition = new Vector2(x, y);
        _feedbackHostPosition = new Vector2(x, y - hostOffset);
        _feedbackText = new TextComponent(
            _connectGroup,
            new Vector2(x, y),
            new Vector2(FormWidth, LabelHeight),
            new Vector2(0.5f, 1f),
            "",
            FeedbackFontSize,
            FontStyle.Normal,
            TextAnchor.UpperCenter
        );
        ((TextComponent) _feedbackText).SetFont(FontManager.UITitleFont);
        _feedbackText.SetActive(false);

        RefreshLanguage();
    }

    private ITextComponent Label(float x, float y, float width, string key) {
        var label = new TextComponent(
            _connectGroup,
            new Vector2(x + InputTextInset, y),
            new Vector2(width - InputTextInset, LabelHeight),
            "",
            LabelFontSize,
            FontStyle.Normal,
            TextAnchor.MiddleLeft
        );
        label.SetFont(FontManager.UITitleFont);
        label.SetColor(Text);
        _labels.Add((label, key));
        return label;
    }

    private static void Style(InputComponent input) {
        input.SetLeftAligned();
        input.SetLineColor(Line);
    }

    private ButtonComponent ModeButton(float x, float y) => new ButtonComponent(
        _connectGroup,
        new Vector2(x, y),
        new Vector2(150f, ButtonComponent.DefaultHeight),
        "",
        LineSprites.Clear,
        FontManager.UITitleFont,
        Text,
        24
    );

    private ButtonComponent ActionButton(float x, float y) => new ButtonComponent(
        _connectGroup,
        new Vector2(x, y),
        new Vector2(FormWidth, ButtonComponent.DefaultHeight + 8f),
        "",
        LineSprites.Clear,
        FontManager.UITitleFont,
        Text,
        28
    );

    private static string Action(string key) => $"<color=#EBB23C>•</color>   {ConnectionFeedback.Get(key)}   <color=#EBB23C>•</color>";

    /// <summary>
    /// Shows only what the selected mode needs. Hosting shares the name and port fields with joining.
    /// </summary>
    private void SetHostMode(bool host) {
        _joinMode.SetTextColor(host ? Muted : Text);
        _hostMode.SetTextColor(host ? Text : Muted);
        _joinUnderline.SetActive(!host);
        _hostUnderline.SetActive(host);

        var portX = host ? _portHostX : _portJoinX;
        _portLabel.SetPosition(new Vector2(portX + InputTextInset, _portLabel.GetPosition().y));
        _portInput.SetPosition(new Vector2(portX, _portInput.GetPosition().y));
        _feedbackText.SetPosition(host ? _feedbackHostPosition : _feedbackJoinPosition);

        _addressLabel.SetActive(!host);
        _addressInput.SetActive(!host);
        _hostHint.SetActive(host);
        _connectionButton.SetActive(!host);
        _serverButton.SetActive(host);
        ClearFeedback();
    }

    private void ResetConnectionButton() {
        _connecting = false;
        _connectionButton.SetText(Action("connect"));
        _connectionButton.SetInteractable(true);
    }

    /// <summary>
    /// Callback method for when the connect button is pressed.
    /// </summary>
    private void OnConnectButtonPressed() {
        var address = _addressInput.GetInput();

        if (address.Length == 0) {
            ShowFeedback(Amber, "addressMissing", _addressInput);
            return;
        }

        var portString = _portInput.GetInput();

        var parsedPort = int.TryParse(portString, out var port);
        if (!parsedPort || port == 0) {
            ShowFeedback(Amber, "portInvalid", _portInput);
            return;
        }

        Logger.Debug($"Connect button pressed, address: {address}:{port}");

        if (!ValidateUsername(out var username)) {
            return;
        }

        // Input values were valid, so we can store them in the settings
        Logger.Debug($"Saving join address {address} in global settings");
        Logger.Debug($"Saving join port {port} in global settings");
        Logger.Debug($"Saving join username {username} in global settings");
        _modSettings.ConnectAddress = address;
        _modSettings.ConnectPort = port;
        _modSettings.Username = username;

        ClearFeedback();

        // Disable the connect button while we are trying to establish a connection
        _connecting = true;
        _connectionButton.SetText(Action("connecting"));
        _connectionButton.SetInteractable(false);

        ConnectButtonPressed?.Invoke(address, port, username);
    }

    /// <summary>
    /// Callback method for when the start hosting button is pressed.
    /// </summary>
    private void OnStartButtonPressed() {
        var portString = _portInput.GetInput();

        var parsedPort = int.TryParse(portString, out var port);
        if (!parsedPort || port == 0) {
            ShowFeedback(Amber, "portInvalid", _portInput);
            return;
        }

        if (!ValidateUsername(out var username)) {
            return;
        }

        ClearFeedback();

        // Start the server in networkManager
        StartHostButtonPressed?.Invoke(username, port);
    }

    /// <summary>
    /// Shows a translated message and marks the fields it refers to.
    /// </summary>
    private void ShowFeedback(Color color, string key, params InputComponent[] fields) {
        ClearFeedback();

        foreach (var field in fields) {
            field.SetLineColor(Amber);
        }

        _feedbackText.SetColor(color);
        _feedbackText.SetText(ConnectionFeedback.Get(key));
        _feedbackText.SetActive(true);

        _feedbackHideCoroutine = MonoBehaviourUtil.Instance.StartCoroutine(WaitHideFeedbackText());
    }

    private void ClearFeedback() {
        if (_feedbackHideCoroutine != null) {
            MonoBehaviourUtil.Instance.StopCoroutine(_feedbackHideCoroutine);
            _feedbackHideCoroutine = null;
        }

        _feedbackText.SetActive(false);
        _usernameInput.SetLineColor(Line);
        _addressInput.SetLineColor(Line);
        _portInput.SetLineColor(Line);
    }

    /// <summary>
    /// Coroutine for hiding the feedback text after a delay.
    /// </summary>
    /// <returns>An enumerator for the coroutine.</returns>
    private IEnumerator WaitHideFeedbackText() {
        yield return new WaitForSeconds(FeedbackTextHideTime);

        _feedbackHideCoroutine = null;
        ClearFeedback();
    }

    private bool ValidateUsername(out string username) {
        username = _usernameInput.GetInput();

        if (username.Length == 0) {
            ShowFeedback(Amber, "nameMissing", _usernameInput);
            return false;
        }

        if (username.Length > ServerManager.UsernameMaxLength) {
            ShowFeedback(Amber, "nameTooLong", _usernameInput);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sprites of the form, drawn here because they are plain shapes that take their color from the image.
    /// </summary>
    private static class LineSprites {
        public static readonly MultiStateSprite Field = States(Draw(true));
        public static readonly MultiStateSprite Clear = States(Draw(false));
        public static readonly Sprite Accent = Solid();
        public static readonly Sprite Backing = Feathered();

        // A wide shadow with no visible edge, so the backing reads as part of the scene and not as a box.
        private static Sprite Feathered() {
            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            for (var px = 0; px < size; px++) {
                for (var py = 0; py < size; py++) {
                    var dx = (px + 0.5f) / size * 2f - 1f;
                    var dy = (py + 0.5f) / size * 2f - 1f;
                    var distance = Mathf.Sqrt(dx * dx + dy * dy);
                    var alpha = 0.9f * Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0.45f, 1f, distance));
                    texture.SetPixel(px, py, new Color(0.015f, 0.02f, 0.03f, alpha));
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, size, size), Vector2.one / 2f);
        }

        private static MultiStateSprite States(Sprite sprite) => new MultiStateSprite {
            Neutral = sprite, Hover = sprite, Active = sprite, Disabled = sprite
        };

        private static Sprite Draw(bool bottomLine) {
            var texture = new Texture2D(8, 8, TextureFormat.ARGB32, false);
            for (var px = 0; px < 8; px++) {
                for (var py = 0; py < 8; py++) {
                    texture.SetPixel(px, py, bottomLine && py < 2 ? Color.white : Color.clear);
                }
            }

            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, 8, 8), Vector2.one / 2f, 100f, 0, SpriteMeshType.FullRect, new Vector4(3, 3, 3, 3));
        }

        private static Sprite Solid() {
            var texture = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            texture.SetPixel(0, 0, Amber);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, 1, 1), Vector2.one / 2f);
        }
    }
}
