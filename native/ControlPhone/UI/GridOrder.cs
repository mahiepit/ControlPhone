using System.Collections.ObjectModel;

namespace ControlPhone;

/// <summary>
/// Đưa danh sách ô trên lưới về đúng danh sách / thứ tự mới mà KHÔNG dựng lại cả lưới: chỉ thêm, bớt, dời
/// những ô thực sự đổi chỗ (giữ nguyên ô + hình của các máy còn lại). Đổi số 1 máy = dời đúng 1 ô.
/// </summary>
public static class GridOrder
{
    /// <returns>Số thao tác (thêm + bớt + dời) đã làm.</returns>
    public static int Sync<T>(ObservableCollection<T> shown, IReadOnlyList<T> target) where T : class
    {
        int ops = 0;
        var want = new HashSet<T>(target);
        for (int i = shown.Count - 1; i >= 0; i--) if (!want.Contains(shown[i])) { shown.RemoveAt(i); ops++; }
        var pos = new Dictionary<T, int>();
        for (int i = 0; i < shown.Count; i++) pos[shown[i]] = i;
        var present = target.Where(pos.ContainsKey).ToList();
        // dãy con tăng dài nhất = các ô đã đúng thứ tự tương đối → đứng yên; các ô còn lại dời ra cuối rồi đặt đúng chỗ
        var keep = Lis(present.Select(d => pos[d]).ToArray()).Select(k => present[k]).ToHashSet();
        foreach (var d in present)
        {
            if (keep.Contains(d)) continue;
            int j = shown.IndexOf(d);
            if (j != shown.Count - 1) { shown.Move(j, shown.Count - 1); ops++; }
        }
        for (int i = 0; i < target.Count; i++)
        {
            if (i < shown.Count && ReferenceEquals(shown[i], target[i])) continue;
            int j = shown.IndexOf(target[i]);
            if (j >= 0) shown.Move(j, i); else shown.Insert(i, target[i]);
            ops++;
        }
        return ops;
    }

    /// <summary>Vị trí (trong a) của một dãy con tăng dài nhất.</summary>
    static List<int> Lis(int[] a)
    {
        var tails = new List<int>();
        var prev = new int[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi) { int m = (lo + hi) / 2; if (a[tails[m]] < a[i]) lo = m + 1; else hi = m; }
            prev[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        var res = new List<int>();
        for (int k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = prev[k]) res.Add(k);
        return res;
    }
}
