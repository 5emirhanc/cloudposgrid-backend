using CloudPosGrid.Application.Common;

namespace CloudPosGrid.Tests;

public class SlugHelperTests
{
    [Theory]
    [InlineData("Köşe Bucak Market", "kose-bucak-market")]
    [InlineData("Şişli Gıda", "sisli-gida")]
    [InlineData("  Çift   Boşluk  ", "cift-bosluk")]
    [InlineData("A--B__C", "a-b-c")]
    public void Slugify_NormalizesTurkishAndSeparators(string input, string expected)
        => Assert.Equal(expected, SlugHelper.Slugify(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Slugify_FallsBackToDefault_WhenEmptyOrSymbolsOnly(string input)
        => Assert.Equal("isletme", SlugHelper.Slugify(input));
}
