using System;
using System.Collections.Generic;
using Hkmp.Game.Settings;
using Hkmp.Networking.Client;
using Hkmp.Ui.Component;
using Hkmp.Util;
using UnityEngine;

namespace Hkmp.Ui;

/// <summary>
/// The list of connected players and their ping, in the top right corner while the player holds the quick map. It
/// follows the game's quick map action, so it is Tab or the left bumper by default and moves with the player's own
/// binding; where the map does not open, holding the button shows the list all the same. It lives in the in-game group,
/// which is hidden outside gameplay and while paused.
/// </summary>
internal class PlayerListInterface {
    /// <summary>The distance from the top and right edges of the screen.</summary>
    private const float ScreenMargin = 30f;

    /// <summary>The width of the list.</summary>
    private const float Width = 360f;

    /// <summary>The height of one row.</summary>
    private const float RowHeight = 34f;

    /// <summary>The width of the ping column.</summary>
    private const float PingWidth = 90f;

    /// <summary>The reference height of the UI canvas.</summary>
    private const float CanvasHeight = 1080f;

    /// <summary>The reference width of the UI canvas.</summary>
    private const float CanvasWidth = 1920f;

    /// <summary>Below this round trip time the ping shows as good.</summary>
    private const int GoodPing = 80;

    /// <summary>Below this round trip time the ping shows as fair; above it, as poor.</summary>
    private const int FairPing = 180;

    private static readonly Color GoodColor = new Color(0.47f, 0.82f, 0.55f);
    private static readonly Color FairColor = new Color(0.9f, 0.78f, 0.39f);
    private static readonly Color PoorColor = new Color(0.9f, 0.43f, 0.39f);
    private static readonly Color DimColor = new Color(0.78f, 0.8f, 0.84f);

    private readonly ComponentGroup _group;
    private readonly ModSettings _modSettings;
    private readonly NetClient _netClient;
    private readonly TextComponent _title;
    private readonly List<(TextComponent Name, TextComponent Ping)> _rows = new();

    /// <summary>
    /// Returns the name and round trip time of each connected player, the local one first.
    /// </summary>
    public Func<List<(string Name, int? Ping)>> Source { get; set; }

    public PlayerListInterface(ComponentGroup group, ModSettings modSettings, NetClient netClient) {
        _group = group;
        _modSettings = modSettings;
        _netClient = netClient;
        group.SetActive(false);

        var left = CanvasWidth - ScreenMargin - Width;
        _title = new TextComponent(
            group,
            new Vector2(left + Width / 2f, CanvasHeight - ScreenMargin - RowHeight / 2f),
            new Vector2(Width, RowHeight),
            ConnectionFeedback.Get("playerListTitle").ToUpperInvariant(),
            UiManager.NormalFontSize,
            alignment: TextAnchor.MiddleLeft
        );
        _title.SetColor(DimColor);

        var rows = PlayerListLayout.RowsIn(CanvasHeight - 2f * ScreenMargin - RowHeight, RowHeight);
        for (var row = 0; row < rows; row++) {
            var y = CanvasHeight - ScreenMargin - RowHeight * (row + 1.5f);
            var name = new TextComponent(
                group,
                new Vector2(left + (Width - PingWidth) / 2f, y),
                new Vector2(Width - PingWidth, RowHeight),
                "",
                UiManager.NormalFontSize,
                alignment: TextAnchor.MiddleLeft
            );
            var ping = new TextComponent(
                group,
                new Vector2(left + Width - PingWidth / 2f, y),
                new Vector2(PingWidth, RowHeight),
                "",
                UiManager.NormalFontSize,
                alignment: TextAnchor.MiddleRight
            );
            _rows.Add((name, ping));
        }

        MonoBehaviourUtil.Instance.OnUpdateEvent += Refresh;
    }

    private void Refresh() {
        var held = _modSettings.ShowPlayerList
                   && _netClient.IsConnected
                   && InputHandler.Instance != null
                   && InputHandler.Instance.inputActions.quickMap.IsPressed;
        var entries = held ? Source?.Invoke() : null;
        _group.SetActive(entries != null);
        if (entries == null) {
            return;
        }

        var (shown, hidden) = PlayerListLayout.Fit(entries.Count, _rows.Count);
        for (var row = 0; row < _rows.Count; row++) {
            var (name, ping) = _rows[row];
            if (row < shown) {
                name.SetText(entries[row].Name);
                name.SetColor(Color.white);
                ping.SetText(entries[row].Ping is { } rtt ? $"{rtt} ms" : "");
                ping.SetColor(ColorOf(entries[row].Ping));
            } else if (row == shown && hidden > 0) {
                name.SetText(string.Format(ConnectionFeedback.Get("playerListMore"), hidden));
                name.SetColor(DimColor);
                ping.SetText("");
            } else {
                name.SetText("");
                ping.SetText("");
            }
        }
    }

    private static Color ColorOf(int? ping) {
        if (ping is not { } rtt) {
            return DimColor;
        }

        return rtt < GoodPing ? GoodColor : rtt < FairPing ? FairColor : PoorColor;
    }
}
