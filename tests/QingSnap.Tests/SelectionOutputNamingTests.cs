using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class SelectionOutputNamingTests
{
    [Fact]
    public void FreeSelectionUsesOneBasedIndexesWithoutPaddingWhenCountIsSingleDigit()
    {
        Assert.Equal("QingSnap_1.png", SelectionOutputNaming.FreeFileName("QingSnap", 1, 5, ".png"));
        Assert.Equal("QingSnap_5.png", SelectionOutputNaming.FreeFileName("QingSnap", 5, 5, ".png"));
    }

    [Fact]
    public void FreeSelectionPadsIndexesToTheWidthOfTheCount()
    {
        Assert.Equal("QingSnap_01.png", SelectionOutputNaming.FreeFileName("QingSnap", 1, 12, ".png"));
        Assert.Equal("QingSnap_12.png", SelectionOutputNaming.FreeFileName("QingSnap", 12, 12, ".png"));
    }

    [Theory]
    [InlineData(1, 1, 3, 3, "QingSnap_1_1.png")]
    [InlineData(3, 3, 3, 3, "QingSnap_3_3.png")]
    [InlineData(1, 1, 12, 12, "QingSnap_01_01.png")]
    [InlineData(12, 12, 12, 12, "QingSnap_12_12.png")]
    [InlineData(1, 3, 12, 3, "QingSnap_01_3.png")]
    [InlineData(12, 3, 12, 3, "QingSnap_12_3.png")]
    public void MatrixSelectionUsesRowAndColumnNumbers(
        int row,
        int column,
        int rows,
        int columns,
        string expected)
    {
        Assert.Equal(expected, SelectionOutputNaming.MatrixFileName("QingSnap", row, column, rows, columns, ".png"));
    }

    [Fact]
    public void PrefixAndExtensionAreNormalized()
    {
        Assert.Equal("QingSnap_1_1.jpg", SelectionOutputNaming.MatrixFileName(
            "QingSnap",
            1,
            1,
            2,
            2,
            "JPG"));
        Assert.Equal("A_B_C_1.png", SelectionOutputNaming.FreeFileName("A/B:C", 1, 1, "png"));
        Assert.Equal("QingSnap_1.png", SelectionOutputNaming.FreeFileName("  ", 1, 1, string.Empty));
    }
}
