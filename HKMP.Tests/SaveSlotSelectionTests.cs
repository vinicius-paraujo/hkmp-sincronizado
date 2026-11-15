using System.Collections.Generic;
using Hkmp.Ui;
using Xunit;

namespace HKMP.Tests;

public class SaveSlotSelectionTests {
    private readonly SaveSlotSelection _selection = new();
    private readonly List<bool> _results = new();

    [Fact]
    public void SelectingASaveRunsTheCallbackOnce() {
        _selection.Open(_results.Add);

        Assert.True(_selection.End(true));
        Assert.False(_selection.End(true));

        Assert.Equal(new[] { true }, _results);
    }

    [Fact]
    public void ASecondOpenWhileOpenIsRefused() {
        Assert.True(_selection.Open(_results.Add));
        Assert.False(_selection.Open(_results.Add));

        _selection.End(true);

        Assert.Equal(new[] { true }, _results);
    }

    [Fact]
    public void CancellingThenHostingAgainRunsOnlyTheNewCallback() {
        var first = new List<bool>();
        _selection.Open(first.Add);
        _selection.End(false);

        _selection.Open(_results.Add);
        _selection.End(true);

        Assert.Equal(new[] { false }, first);
        Assert.Equal(new[] { true }, _results);
    }

    [Fact]
    public void EndWithoutOpenDoesNothing() {
        Assert.False(_selection.End(false));
        Assert.False(_selection.IsOpen);
    }

    [Fact]
    public void TheCallbackMayOpenANewSelection() {
        _selection.Open(_ => _selection.Open(_results.Add));
        _selection.End(false);

        Assert.True(_selection.IsOpen);
    }
}
