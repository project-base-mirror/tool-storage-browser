using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed class NavigationHistoryCoordinator
{
    private readonly List<S3Location> _entries = [];
    private int _index = -1;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public int Count => _entries.Count;

    public bool Record(S3Location location)
    {
        if (_index >= 0 && _entries[_index] == location)
            return false;

        if (_index < _entries.Count - 1)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

        _entries.Add(location);
        _index = _entries.Count - 1;
        return true;
    }

    public bool TryMove(int delta, out S3Location location)
    {
        var next = _index + delta;
        if (next < 0 || next >= _entries.Count)
        {
            location = default;
            return false;
        }

        _index = next;
        location = _entries[_index];
        return true;
    }
}
