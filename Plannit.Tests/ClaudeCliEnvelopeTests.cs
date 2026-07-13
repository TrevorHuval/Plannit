using Plannit.Services.Ai;

namespace Plannit.Tests;

public class ClaudeCliEnvelopeTests
{
    [Fact]
    public void Success_ReturnsResultText()
    {
        var json = """{"type":"result","is_error":false,"result":"{\"suggestions\":[]}"}""";
        var (text, error) = ClaudeCliProvider.InterpretEnvelope(json);

        Assert.Null(error);
        Assert.Equal("{\"suggestions\":[]}", text);
    }

    [Fact]
    public void ApiError_SurfacesResultAsError()
    {
        // The CLI reports auth/permission failures with is_error=true (and exit code 1),
        // carrying the human message in "result".
        var json = """{"type":"result","is_error":true,"result":"Not logged in · Please run /login"}""";
        var (text, error) = ClaudeCliProvider.InterpretEnvelope(json);

        Assert.Null(text);
        Assert.Equal("Not logged in · Please run /login", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"no_result_field\":true}")]
    public void NonEnvelope_ReturnsNeither(string raw)
    {
        var (text, error) = ClaudeCliProvider.InterpretEnvelope(raw);
        Assert.Null(text);
        Assert.Null(error);
    }
}
