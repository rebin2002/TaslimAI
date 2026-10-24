using Taslim.Api.Localization;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class LanguagePackCatalogTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    [InlineData("ku")]
    [InlineData("zh-CN")]
    [InlineData("ZH-cn")]
    public void Accepts_supported_interface_language_codes(string value)
    {
        Assert.True(LanguagePackCatalog.IsInterfaceLanguage(value));
    }

    [Fact]
    public void Normalizes_simplified_chinese_to_canonical_bcp47_code()
    {
        Assert.Equal("zh-CN", LanguagePackCatalog.NormalizeInterfaceLanguage(" zh-cn "));
        Assert.Equal("en", LanguagePackCatalog.NormalizeInterfaceLanguage(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("zh")]
    public void Rejects_unsupported_interface_language_codes(string value)
    {
        Assert.False(LanguagePackCatalog.IsInterfaceLanguage(value));
    }
}
