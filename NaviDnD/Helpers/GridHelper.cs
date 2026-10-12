namespace NaviDnD.Helpers;

public class GridHelper
{
    public static bool HasAdjacentTwos(List<int> arr)
    {
        int n = arr.Count;

        // Check all adjacent pairs
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n; // Next index, wrapping around to the start

            if (arr[i] == 2 && arr[next] == 2)
                return true;
        }

        return false;
    }

    public static List<int> Transform1112List(List<int> list)
    {
        // Find the index of the 2
        int twoIndex = list.IndexOf(2);
        if (twoIndex == -1) return list;

        // Copy the list
        List<int> result = new List<int>(list);

        // Walk through all indices
        for (int i = 0; i < 4; i++)
        {
            // Skip the 2 and its neighbours
            if (i == twoIndex ||
                i == (twoIndex + 1) % 4 ||
                i == (twoIndex + 3) % 4)  // (twoIndex - 1 + 4) % 4
            {
                continue;
            }

            // A 1 that is not adjacent becomes 0
            if (result[i] == 1)
            {
                result[i] = 0;
                return result;
            }
        }

        return result;
    }
}
