namespace Seiza.App.Models;

/// <summary>UI-thread policy for coalescing settings changes until native work is idle.</summary>
internal sealed class DeferredSettingsUpdate
{
    private bool _pending;
    public void Request() => _pending = true;

    public bool TryTake(bool closed, bool busy, int activeJobs)
    {
        if (closed) { _pending = false; return false; }
        if (!_pending || busy || activeJobs != 0) return false;
        _pending = false;
        return true;
    }
}
