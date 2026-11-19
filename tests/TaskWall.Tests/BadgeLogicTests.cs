using TaskWall;
using Xunit;

namespace TaskWall.Tests;

public class BadgeLogicTests
{
    [Theory]
    [InlineData("(3) Messenger", 3)]
    [InlineData("(1) Discord | #general", 1)]
    [InlineData("Telegram (12)", 12)]
    [InlineData("Inbox - Gmail", 0)]
    [InlineData("(2024) Budżet.xlsx - Excel", 0)]   // a year in a file's name
    [InlineData("(1) Intro.mp4 - VLC media player", 0)]
    [InlineData("Raport (2).docx - Word", 0)]        // a copy's number
    [InlineData("(1234) Too many", 0)]               // four digits: not a counter
    [InlineData("", 0)]
    public void TitleCount(string title, int expected) => Assert.Equal(expected, BadgeLogic.TitleCount(title));
}
