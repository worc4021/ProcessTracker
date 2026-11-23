using System.Text.Json;
using ProcessTracker.Cli.Admin;

namespace ProcessTracker.Cli.Tests.Admin;

public class SettingsCarryOverTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Rewrite_KeepsKnownValidKeys_DropsOthers()
    {
        var output = Parse(SettingsCarryOver.Rewrite("""{"RetentionHours":48,"DataDir":"D:\\x","Kestrel":{}}"""));
        Assert.Equal(48, output.GetProperty("RetentionHours").GetDouble());
        Assert.False(output.TryGetProperty("DataDir", out _));
        Assert.False(output.TryGetProperty("Kestrel", out _));
    }

    [Fact]
    public void Rewrite_KeepsAllThreeKeys()
    {
        var text = SettingsCarryOver.Rewrite("""{"RetentionHours":2.5,"OrphanIdleAlertMinutes":10,"UserProfilePath":"C:\\Users\\me"}""");
        var output = Parse(text);
        Assert.Equal(2.5, output.GetProperty("RetentionHours").GetDouble());
        Assert.Equal(10, output.GetProperty("OrphanIdleAlertMinutes").GetDouble());
        Assert.Equal(@"C:\Users\me", output.GetProperty("UserProfilePath").GetString());
        Assert.Contains('\n', text); // indented
    }

    [Fact]
    public void Rewrite_KeepsValidMatchingSettings()
    {
        var output = Parse(SettingsCarryOver.Rewrite("""
            {"OrphanAlertImages":["solver.exe","engine*.exe"],
             "ActivityThresholds":{"solver.exe":{"CpuCores":0.05,"ContextSwitches":2000}},
             "HeavyCommandPatterns":["\bsolver\b"]}
            """));
        Assert.Equal(["solver.exe", "engine*.exe"], output.GetProperty("OrphanAlertImages").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(2000, output.GetProperty("ActivityThresholds").GetProperty("solver.exe").GetProperty("ContextSwitches").GetInt64());
        Assert.Equal(0.05, output.GetProperty("ActivityThresholds").GetProperty("solver.exe").GetProperty("CpuCores").GetDouble());
        Assert.Single(output.GetProperty("HeavyCommandPatterns").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"OrphanAlertImages":["ok.exe",""]}""")]
    [InlineData("""{"OrphanAlertImages":"solver.exe"}""")]
    [InlineData("""{"OrphanAlertImages":[1]}""")]
    [InlineData("""{"ActivityThresholds":{"solver.exe":{"CpuCores":-1}}}""")]
    [InlineData("""{"ActivityThresholds":{"solver.exe":{"IoOps":"many"}}}""")]
    [InlineData("""{"ActivityThresholds":[1]}""")]
    [InlineData("""{"HeavyCommandPatterns":["(unclosed"]}""")]
    [InlineData("""{"HeavyCommandPatterns":[""]}""")]
    public void Rewrite_InvalidMatchingSettings_AreDropped(string input)
    {
        Assert.Empty(Parse(SettingsCarryOver.Rewrite(input)).EnumerateObject());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"RetentionHours\":")]
    public void Rewrite_InvalidOrMissingInput_ReturnsEmptyObject(string? input)
    {
        Assert.Empty(Parse(SettingsCarryOver.Rewrite(input)).EnumerateObject());
    }

    [Theory]
    [InlineData("""{"RetentionHours":-1}""")]
    [InlineData("""{"RetentionHours":0}""")]
    [InlineData("""{"RetentionHours":10000}""")]
    [InlineData("""{"RetentionHours":"48"}""")]
    [InlineData("""{"OrphanIdleAlertMinutes":0}""")]
    [InlineData("""{"OrphanIdleAlertMinutes":10081}""")]
    [InlineData("""{"UserProfilePath":""}""")]
    [InlineData("""{"UserProfilePath":"relative\\path"}""")]
    [InlineData("""{"UserProfilePath":"\\\\server\\share\\me"}""")]
    [InlineData("""{"UserProfilePath":5}""")]
    [InlineData("""{"UserProfilePath":"/\\server\\share\\me"}""")]
    [InlineData("""{"UserProfilePath":"\\/server\\share\\me"}""")]
    [InlineData("""{"UserProfilePath":"\\??\\UNC\\server\\share"}""")]
    [InlineData("""{"UserProfilePath":"\\\\?\\C:\\x"}""")]
    [InlineData("""{"UserProfilePath":"C:relative"}""")]
    public void Rewrite_DropsOutOfRangeValues(string input)
    {
        Assert.Empty(Parse(SettingsCarryOver.Rewrite(input)).EnumerateObject());
    }

    [Fact]
    public void Rewrite_AcceptsRangeBoundaries()
    {
        var output = Parse(SettingsCarryOver.Rewrite("""{"RetentionHours":720,"OrphanIdleAlertMinutes":10080}"""));
        Assert.Equal(720, output.GetProperty("RetentionHours").GetDouble());
        Assert.Equal(10080, output.GetProperty("OrphanIdleAlertMinutes").GetDouble());
    }
}
