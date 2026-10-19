using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests.Collections;

public sealed class WqlGuardTests
{
    [Theory]
    [InlineData("select * from SMS_R_System")]
    [InlineData("  SELECT SMS_R_System.Name from SMS_R_System where SMS_R_System.Name like '%;%'")]
    [InlineData("select Name from SMS_R_System where Name = \"a;b\"")]
    [InlineData("select Name from SMS_R_System where Name = 'O\\'Brien; x'")]
    [InlineData("select\r\n  Name\tfrom SMS_R_System")]
    [InlineData("select * from SMS_R_System where SystemGroupName = 'CONTOSO\\\\Group'")]
    public void AReadOnlySelectIsAccepted(string expression)
    {
        Assert.Null(WqlGuard.Check(expression));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delete from SMS_R_System")]
    [InlineData("selectx * from SMS_R_System")]
    [InlineData("associators of {SMS_R_System.ResourceId=1}")]
    [InlineData("references of {SMS_R_System.ResourceId=1}")]
    [InlineData("select * from SMS_R_System; delete from SMS_R_System")]
    [InlineData("select * from SMS_R_System;")]
    [InlineData("select * from SMS_R_System where Name = 'unclosed")]
    [InlineData("select * from SMS_R_System where Name = \"unclosed")]
    [InlineData("select * from SMS_R_System where Name = 'x\\'")]
    [InlineData("select * from SMS_R_System\0")]
    [InlineData("select * from SMS_R_System\u001b[2J")]
    [InlineData("select * from SMS_R_System\u2028")]
    [InlineData("/* x */ select * from SMS_R_System")]
    public void AnythingElseIsRefusedWithAPlainReason(string? expression)
    {
        var reason = WqlGuard.Check(expression);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.DoesNotContain("Exception", reason);
    }

    [Theory]
    [InlineData("select * from SMS_R_System where Name = 'O\\'Brien'")]
    [InlineData("select * from SMS_R_System where Name = \"O\\\"B; x\"")]
    [InlineData("select * from SMS_R_System where Name like 'C:\\\\'")]
    [InlineData("select * from SMS_R_System where Name = ''")]
    [InlineData("select * from SMS_R_System where Name = \"it's; fine\"")]
    public void ABackslashEscapedQuoteAndAnEmptyValueStayInsideTheirValue(string expression)
    {
        Assert.Null(WqlGuard.Check(expression));
    }

    [Theory]
    [InlineData("select * from SMS_R_System where Name = 'O''Brien'")]
    [InlineData("select * from SMS_R_System where Name = 'x'';delete from SMS_R_System'")]
    [InlineData("select * from SMS_R_System where Name = \"a\"\"b\"")]
    [InlineData("select * from SMS_R_System where Name = '''")]
    public void ADoubledQuoteIsRefusedBecauseTheProviderReadsItAsTwoValues(string expression)
    {
        var reason = WqlGuard.Check(expression);
        Assert.NotNull(reason);
        Assert.Contains("Two quotes in a row", reason);
    }

    [Theory]
    [InlineData("select * from SMS_R_System where Name like 'C:\\\\'; delete from SMS_R_System")]
    [InlineData("select * from SMS_R_System where Name = \"it's\"; delete from SMS_R_System")]
    [InlineData("select * from SMS_R_System where Name = 'x\\' ; delete from SMS_R_System")]
    [InlineData("select * from SMS_R_System where Name = 'a' ; select * from SMS_R_User")]
    [InlineData("select * from SMS_R_System where Name = 'a\\\\\\'; delete")]
    public void ASecondStatementAfterAnEscapeIsStillRefused(string expression)
    {
        Assert.NotNull(WqlGuard.Check(expression));
    }

    [Fact]
    public void AnOverlongExpressionIsRefused()
    {
        var text = "select * from SMS_R_System where Name = '" + new string('a', WqlGuard.MaxLength) + "'";
        Assert.NotNull(WqlGuard.Check(text));
    }

    [Theory]
    [InlineData("Rule 1", true)]
    [InlineData("Rule *", true)]
    [InlineData("Rule ?", true)]
    [InlineData("Win [x64]", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("tab\tname", false)]
    public void RuleNamesWithoutControlCharactersAreAcceptedWildcardCharactersIncluded(string name, bool valid)
    {
        Assert.Equal(valid, WqlGuard.CheckRuleName(name) is null);
    }
}
