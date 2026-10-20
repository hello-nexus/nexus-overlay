using System.Collections.Generic;

namespace Nexus.Overlay;

internal static class ProcessTree
{
    // Descendants of root; cycle-safe, since a dead parent's id can be reused.
    public static HashSet<int> Descendants(int root, IReadOnlyList<(int Pid, int ParentPid)> procs)
    {
        var found = new HashSet<int>();
        var frontier = new Queue<int>();
        frontier.Enqueue(root);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var (pid, parentPid) in procs)
            {
                if (parentPid == parent && pid != root && found.Add(pid)) frontier.Enqueue(pid);
            }
        }
        return found;
    }
}
