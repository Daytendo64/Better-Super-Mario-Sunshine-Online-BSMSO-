using SMSO.Net;

namespace SMSO.Tests;

public class PlayerNameValidatorTests
{
    [Theory]
    [InlineData("Mr.Smith!")]
    [InlineData("Player-1")]
    [InlineData("what?")]
    [InlineData("a.b.c")]
    [InlineData("hello_world")]
    [InlineData("Player One")]
    public void ValidNames_AreAccepted(string name)
    {
        Assert.True(PlayerNameValidator.TryValidate(name, out var error), error);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name\nwith\nnewlines")]
    public void InvalidNames_AreRejected(string name)
    {
        Assert.False(PlayerNameValidator.TryValidate(name, out _));
    }
}
