using System;

namespace Hkmp.Ui;

/// <summary>
/// Tracks one save slot selection from the multiplayer menu, so that its callback runs exactly once no matter how
/// the selection ends.
/// </summary>
internal class SaveSlotSelection {
    /// <summary>
    /// The callback of the selection that is open, or null if none is.
    /// </summary>
    private Action<bool> _callback;

    /// <summary>
    /// Whether a selection is open.
    /// </summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// Opens a selection with the given callback.
    /// </summary>
    /// <param name="callback">Receives whether a save was selected. Can be null.</param>
    /// <returns>False if a selection was already open, in which case it is left as it was. A second selection on
    /// top of the first would run the hosting or joining a second time.</returns>
    public bool Open(Action<bool> callback) {
        if (IsOpen) {
            return false;
        }

        IsOpen = true;
        _callback = callback;
        return true;
    }

    /// <summary>
    /// Ends the open selection and runs its callback. Does nothing if no selection is open, because the game can
    /// report the end of a selection more than once.
    /// </summary>
    /// <param name="saveSelected">Whether a save was selected.</param>
    /// <returns>Whether a selection was open and has now ended.</returns>
    public bool End(bool saveSelected) {
        if (!IsOpen) {
            return false;
        }

        // Closed before the callback runs, so the callback may open a new selection
        var callback = _callback;
        IsOpen = false;
        _callback = null;

        callback?.Invoke(saveSelected);
        return true;
    }
}
