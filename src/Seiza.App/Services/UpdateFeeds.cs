namespace Seiza.App.Services;

/// <summary>
/// The two copies of the signed Windows feed, and the rule for choosing one.
/// </summary>
/// <remarks>
/// Release CI publishes appcast.xml and appcast.xml.signature on the GitHub
/// release, and updates.seiza.fyi mirrors both byte for byte. NetSparkle runs
/// in strict mode, so a copy that differs from what CI signed fails
/// verification. Builds released before the mirror read only the GitHub copy,
/// so CI must keep publishing it there. The Mac app has its own feed under
/// /mac/ because the two apps version and release separately.
/// </remarks>
internal static class UpdateFeeds
{
    public const string Primary = "https://updates.seiza.fyi/windows/appcast.xml";

    public const string GitHub =
        "https://github.com/theatrus/seiza-win/releases/latest/download/appcast.xml";

    public static IReadOnlyList<string> AppCastUrls { get; } = [Primary, GitHub];

    /// <summary>
    /// Asks each feed in order and returns the first answer. A feed answers
    /// when it was reached and verified, whether or not it offers an update;
    /// one that throws or cannot be verified passes to the next. Returns null
    /// when no feed answered, with the last exception, if any.
    /// </summary>
    public static async Task<(TFeed Feed, TResult Result)?> FirstAnswerAsync<TFeed, TResult>(
        IEnumerable<TFeed> feeds,
        Func<TFeed, Task<TResult>> check,
        Func<TResult, bool> answered,
        Action<Exception>? failed = null)
    {
        foreach (TFeed feed in feeds)
        {
            TResult result;
            try
            {
                result = await check(feed);
            }
            catch (Exception exception)
            {
                failed?.Invoke(exception);
                continue;
            }

            if (answered(result))
            {
                return (feed, result);
            }
        }

        return null;
    }
}
