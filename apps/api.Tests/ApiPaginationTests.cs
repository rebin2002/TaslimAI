using Taslim.Api.Infrastructure;
using Xunit;

namespace Taslim.Api.Tests;

public sealed class ApiPaginationTests
{
    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(10_001, ApiPagination.MaxPage)]
    [InlineData(int.MaxValue, ApiPagination.MaxPage)]
    public void NormalizePage_bounds_invalid_and_deep_values(int input, int expected)
    {
        Assert.Equal(expected, ApiPagination.NormalizePage(input));
    }

    [Theory]
    [InlineData(-10, 1)]
    [InlineData(0, 1)]
    [InlineData(100, 100)]
    [InlineData(101, ApiPagination.MaxPageSize)]
    [InlineData(int.MaxValue, ApiPagination.MaxPageSize)]
    public void NormalizePageSize_bounds_invalid_and_large_values(int input, int expected)
    {
        Assert.Equal(expected, ApiPagination.NormalizePageSize(input));
    }

    [Fact]
    public void GetOffset_is_bounded_and_cannot_overflow()
    {
        Assert.Equal((ApiPagination.MaxPage - 1) * ApiPagination.MaxPageSize, ApiPagination.GetOffset(int.MaxValue, int.MaxValue));
        Assert.Equal(0, ApiPagination.GetOffset(int.MinValue, int.MinValue));
    }
}
