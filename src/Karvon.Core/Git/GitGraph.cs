namespace Karvon.Git;

/// <summary>
/// A row of the history's graph: the commit's lane and color, and its lines in two halves: from the row's top to its
/// middle (the lanes coming down, those of the commit's children joining it), and from the middle to the bottom (the
/// lanes going on, the commit's parents).
/// </summary>
public sealed record GitGraphRow(int Lane, int Color, int Lanes, IReadOnlyList<GitGraphLine> Top, IReadOnlyList<GitGraphLine> Bottom);

/// <summary>A line of a half row, from a lane to another, in a lane's color.</summary>
public readonly record struct GitGraphLine(int From, int To, int Color);

/// <summary>
/// Lays out the history's graph as the commits come, newest first in topological order, page after page: each lane
/// waits for a commit (a parent of one above); a commit takes the lane that waits for it (the leftmost, when several do:
/// the others join it), or a free one; its first parent goes on in its lane, the others in theirs.
/// </summary>
public sealed class GitGraph
{
    readonly List<string?> _lanes = [];
    readonly List<int> _colors = [];
    int _nextColor;

    public GitGraphRow Add(GitCommit commit)
    {
        var lane = _lanes.IndexOf(commit.Hash);
        if (lane < 0)
        {
            lane = _lanes.IndexOf(null);
            if (lane < 0)
            {
                lane = _lanes.Count;
                _lanes.Add(null);
                _colors.Add(0);
            }
            _colors[lane] = _nextColor++;
        }
        var color = _colors[lane];
        var top = new List<GitGraphLine>();
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i] is null)
                continue;
            if (_lanes[i] == commit.Hash)
            {
                top.Add(new GitGraphLine(i, lane, _colors[i]));
                if (i != lane)
                    _lanes[i] = null;
            }
            else
                top.Add(new GitGraphLine(i, i, _colors[i]));
        }

        // The first parent goes on in the commit's lane; the others where they are awaited, or in a lane of their own.
        _lanes[lane] = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        var bottom = new List<GitGraphLine>();
        for (var p = 1; p < commit.Parents.Count; p++)
        {
            var parent = commit.Parents[p];
            var at = _lanes.IndexOf(parent);
            if (at < 0)
            {
                at = _lanes.IndexOf(null);
                if (at < 0)
                {
                    at = _lanes.Count;
                    _lanes.Add(null);
                    _colors.Add(0);
                }
                _lanes[at] = parent;
                _colors[at] = _nextColor++;
            }
            bottom.Add(new GitGraphLine(lane, at, _colors[at]));
        }
        for (var i = 0; i < _lanes.Count; i++)
            if (_lanes[i] is not null)
                bottom.Add(new GitGraphLine(i, i, _colors[i]));

        while (_lanes.Count > 0 && _lanes[^1] is null)
        {
            _lanes.RemoveAt(_lanes.Count - 1);
            _colors.RemoveAt(_colors.Count - 1);
        }
        var lanes = Math.Max(lane + 1, Math.Max(top.Count == 0 ? 0 : top.Max(l => Math.Max(l.From, l.To)) + 1, bottom.Count == 0 ? 0 : bottom.Max(l => Math.Max(l.From, l.To)) + 1));
        return new GitGraphRow(lane, color, lanes, top, bottom);
    }
}
