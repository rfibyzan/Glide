using Glide.Core;
using Xunit;

namespace Glide.Core.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("2+3", 5)]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("10/4", 2.5)]
    [InlineData("-3+1", -2)]
    [InlineData("-(2+3)", -5)]
    [InlineData("1.5*2", 3)]
    [InlineData("  7  -  2 ", 5)]
    public void TryEvaluate_RespectsPrecedenceParenthesesAndSigns(string expression, double expected)
    {
        Assert.True(Calculator.TryEvaluate(expression, out double result));
        Assert.Equal(expected, result, precision: 10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2+")]
    [InlineData("(1+2")]
    [InlineData("abc")]
    [InlineData("1..2")]
    [InlineData("2 3")]
    public void TryEvaluate_RejectsMalformedExpressions(string expression)
    {
        Assert.False(Calculator.TryEvaluate(expression, out _));
    }

    [Fact]
    public void TryEvaluate_DivisionByZeroReportsNaNInsteadOfFailing()
    {
        Assert.True(Calculator.TryEvaluate("1/0", out double result));
        Assert.True(double.IsNaN(result));
    }
}
