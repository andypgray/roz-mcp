using Serilog.Events;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

public class SerilogConfigurationTests
{
    [Fact]
    public void ParseLogLevel_NumericString_FallsBackToWarning()
    {
        // A numeric ROZ_LOG_LEVEL ("99") binds to an *undefined* enum value via
        // Enum.TryParse; it must fall back to Warning rather than feed a bogus level into
        // LevelConvert.
        SerilogConfiguration.ParseLogLevel("99").ShouldBe(LogEventLevel.Warning);
    }

    [Theory]
    [InlineData("Debug", LogEventLevel.Debug)] // Microsoft.Extensions.Logging name
    [InlineData("Warning", LogEventLevel.Warning)] // shared name
    [InlineData("Verbose", LogEventLevel.Verbose)] // Serilog-only name
    [InlineData("Fatal", LogEventLevel.Fatal)] // Serilog-only name
    public void ParseLogLevel_ValidName_Parses(string value, LogEventLevel expected) => SerilogConfiguration.ParseLogLevel(value).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void ParseLogLevel_NullOrUnrecognised_FallsBackToWarning(string? value) => SerilogConfiguration.ParseLogLevel(value).ShouldBe(LogEventLevel.Warning);
}
