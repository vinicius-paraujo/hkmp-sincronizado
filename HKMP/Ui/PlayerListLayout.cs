namespace Hkmp.Ui;

/// <summary>
/// How many players the list shows in the height it has. When all of them fit, all are shown; otherwise the last
/// row that fits says how many more there are, so the list never runs off the screen.
/// </summary>
internal static class PlayerListLayout {
    /// <summary>
    /// Fits the players into the rows the height holds.
    /// </summary>
    /// <param name="players">How many players there are.</param>
    /// <param name="rows">How many rows fit in the height.</param>
    /// <returns>How many players are shown by name, and how many are summed up in the last row.</returns>
    public static (int Shown, int Hidden) Fit(int players, int rows) {
        if (players <= 0 || rows <= 0) {
            return (0, 0);
        }

        if (players <= rows) {
            return (players, 0);
        }

        // The last row sums up the rest, so one row fewer shows names.
        var shown = rows - 1;
        return (shown, players - shown);
    }

    /// <summary>
    /// How many rows fit in the height.
    /// </summary>
    /// <param name="height">The height the rows may take.</param>
    /// <param name="rowHeight">The height of one row.</param>
    /// <returns>The number of rows.</returns>
    public static int RowsIn(float height, float rowHeight) {
        return rowHeight <= 0f || height <= 0f ? 0 : (int) (height / rowHeight);
    }
}
