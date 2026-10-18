namespace Seiza.App.Models;

/// <summary>Display-only projection; never opens or modifies a native stack.</summary>
internal sealed record LiveStackOverviewItem(
    string Key, string Name, string State, string Counts, string Message,
    string Placeholder, RenderedImageData? Preview)
{
    public static LiveStackOverviewItem Create(LiveStackFilterIdentity filter,
        LiveStackRunSnapshot snapshot, bool completed = false, string? error = null)
    {
        completed |= snapshot.State == LiveStackRunState.Completed;
        string state = error is not null ? "Needs attention" : completed ? "Completed" : StateTitle(snapshot.State);
        string message = error ?? (completed ? "Finished stack; select for details." :
            snapshot.State is LiveStackRunState.Faulted or LiveStackRunState.NeedsAttention
                ? snapshot.Attention.Count > 0 ? snapshot.Attention[^1].Message : snapshot.StatusMessage : snapshot.StatusMessage);
        return new(filter.Key, filter.DisplayName, state,
            $"{snapshot.AcceptedFrames:N0} accepted · {snapshot.RejectedFrames:N0} rejected · {snapshot.IgnoredFrames + snapshot.UnreadableFrames:N0} skipped",
            message,
            completed ? "Completed — no preview retained" : error is not null || snapshot.State == LiveStackRunState.Faulted
                ? "Preview unavailable — see status" : snapshot.HasStack
                    ? "Waiting for preview…" : "Waiting for the first light…",
            snapshot.Preview);
    }

    public static int ColumnsFor(double width, int count) =>
        Math.Min(Math.Max(count, 1), !double.IsFinite(width) || width < 560 ? 1 : width < 840 ? 2 : 3);

    internal static string StateTitle(LiveStackRunState state) => state switch
    {
        LiveStackRunState.Created => "Starting",
        LiveStackRunState.WaitingForLight => "Waiting for lights",
        LiveStackRunState.SavingSnapshot => "Saving snapshot",
        LiveStackRunState.NeedsAttention => "Needs attention",
        _ => state.ToString(),
    };
}
