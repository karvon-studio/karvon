using System.Collections.ObjectModel;

namespace Karvon.Ui;

/// <summary>A collection the UI shows, kept in step with a list.</summary>
public static class ObservableSync
{
    /// <summary>Makes <paramref name="target"/> show <paramref name="items"/> by moving, adding and removing, not by starting over.</summary>
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items) where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i]))
                continue;
            var old = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], items[i]))
                {
                    old = j;
                    break;
                }
            if (old >= 0)
                target.Move(old, i);
            else
                target.Insert(i, items[i]);
        }
        while (target.Count > items.Count)
            target.RemoveAt(target.Count - 1);
    }
}
