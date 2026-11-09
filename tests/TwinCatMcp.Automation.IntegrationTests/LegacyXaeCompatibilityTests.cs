using TwinCatMcp.Automation;
using Xunit;

namespace TwinCatMcp.Automation.IntegrationTests;

public class LegacyXaeCompatibilityTests
{
    [Theory]
    [InlineData(615, 0, "Gvl")]
    [InlineData(602, 0, "Program")]
    [InlineData(606, 0, "Dut")]
    [InlineData(601, 0, "Folder")]
    [InlineData(5, 9099, "Other(9099)")]
    public void PlcKindsUseItemTypeWithoutBreakingIoSubtypes(int type, int subtype, string kind)
        => Assert.Equal(kind, XaeShellSession.DescribeTreeItemKind(type, subtype));

    [Fact]
    public void BuildSelectionMatchesConfigurationAndPlatformInsteadOfFirstName()
    {
        (string, string)[] choices = [("Release", "TwinCAT RT (x64)"), ("Release", "TwinCAT CE7 (ARMV7)"), ("Debug", "TwinCAT CE7 (ARMV7)")];
        Assert.Equal(2, SolutionConfigurationSelection.Select("Release|TwinCAT CE7 (ARMV7)", choices));
        Assert.Equal(3, SolutionConfigurationSelection.Select("Debug", choices));
        Assert.Throws<ArgumentException>(() => SolutionConfigurationSelection.Select("Release", choices));
        Assert.Throws<ArgumentException>(() => SolutionConfigurationSelection.Select("Release|Missing", choices));
        Assert.Throws<ArgumentException>(() => SolutionConfigurationSelection.Select("Release||x86", choices));
    }
}
