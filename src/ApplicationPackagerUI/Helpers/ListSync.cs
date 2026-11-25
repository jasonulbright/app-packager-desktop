using System.Collections.ObjectModel;

namespace ApplicationPackagerUI.Helpers;

/// <summary>Changes a list that a grid shows in place, so the grid keeps its sort, its scroll position and its focused row.</summary>
public static class ListSync
{
    /// <summary>Makes <paramref name="shown"/> equal to <paramref name="wanted"/> with the fewest changes: items leave, arrive and move; the others stay.</summary>
    public static void Sync<T>(ObservableCollection<T> shown, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted);
        for (var i = shown.Count - 1; i >= 0; i--)
            if (!keep.Contains(shown[i])) shown.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < shown.Count && ReferenceEquals(shown[i], wanted[i])) continue;
            var at = shown.IndexOf(wanted[i]);
            if (at >= 0) shown.Move(at, i);
            else shown.Insert(i, wanted[i]);
        }
    }
}
