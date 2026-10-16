using Seiza.App.Services;
using Xunit;

namespace Seiza.App.Tests;

public sealed class UpdateFeedsTests
{
    [Fact]
    public void OurFeedComesBeforeGitHub()
    {
        Assert.Equal(
            ["https://updates.seiza.fyi/windows/appcast.xml",
             "https://github.com/theatrus/seiza-win/releases/latest/download/appcast.xml"],
            UpdateFeeds.AppCastUrls);
    }

    [Fact]
    public async Task TheFirstFeedThatAnswersDecides()
    {
        var asked = new List<string>();
        var answer = await UpdateFeeds.FirstAnswerAsync(
            UpdateFeeds.AppCastUrls,
            feed => { asked.Add(feed); return Task.FromResult("up to date"); },
            _ => true);

        Assert.Equal((UpdateFeeds.Primary, "up to date"), answer);
        Assert.Equal([UpdateFeeds.Primary], asked);
    }

    [Fact]
    public async Task AFeedThatThrowsPassesToGitHub()
    {
        var errors = new List<Exception>();
        var answer = await UpdateFeeds.FirstAnswerAsync(
            UpdateFeeds.AppCastUrls,
            feed => feed == UpdateFeeds.Primary
                ? Task.FromException<string>(new HttpRequestException("unreachable"))
                : Task.FromResult("update available"),
            _ => true,
            errors.Add);

        Assert.Equal((UpdateFeeds.GitHub, "update available"), answer);
        Assert.Single(errors);
    }

    [Fact]
    public async Task AFeedThatCannotBeVerifiedPassesToGitHub()
    {
        var answer = await UpdateFeeds.FirstAnswerAsync(
            UpdateFeeds.AppCastUrls,
            feed => Task.FromResult(feed == UpdateFeeds.Primary ? "could not determine" : "up to date"),
            result => result != "could not determine");

        Assert.Equal((UpdateFeeds.GitHub, "up to date"), answer);
    }

    [Fact]
    public async Task NoAnswerKeepsTheLastError()
    {
        Exception? last = null;
        var answer = await UpdateFeeds.FirstAnswerAsync(
            UpdateFeeds.AppCastUrls,
            feed => Task.FromException<string>(new HttpRequestException(feed)),
            _ => true,
            exception => last = exception);

        Assert.Null(answer);
        Assert.Equal(UpdateFeeds.GitHub, last?.Message);
    }
}
