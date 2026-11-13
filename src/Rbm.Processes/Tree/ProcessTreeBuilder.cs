namespace Rbm.Processes;

/// <summary>Nó da árvore de processos.</summary>
public sealed record ProcessTreeNode(ProcessEntry Process, IReadOnlyList<ProcessTreeNode> Children, int Depth,
    // true quando o PID pai informado não existe mais (ou foi reutilizado por um processo mais novo).
    bool ParentMissing);

/// <summary>
/// Constrói a árvore pai/filho. Um pai só é aceito se existir E tiver sido criado antes (ou ao mesmo tempo)
/// do filho — o Windows reutiliza PIDs, então um "pai" mais novo que o filho é outro processo.
/// Processos cujo pai não existe viram raízes (órfãos). Ciclos (dados inconsistentes) são quebrados.
/// </summary>
public static class ProcessTreeBuilder
{
    public static IReadOnlyList<ProcessTreeNode> Build(IReadOnlyList<ProcessEntry> processes)
    {
        var parents = ResolveParents(processes.Select(p => (p.Pid, p.ParentPid, p.CreateTime)).ToList());
        var children = new List<int>[processes.Count];
        var roots = new List<int>();
        for (int i = 0; i < processes.Count; i++)
        {
            if (parents[i] < 0) roots.Add(i);
            else (children[parents[i]] ??= new List<int>()).Add(i);
        }

        var visited = new bool[processes.Count];
        var result = new List<ProcessTreeNode>(roots.Count);
        foreach (var r in roots) result.Add(BuildNode(r, 0));
        // Nós não alcançados (ciclo) viram raízes.
        for (int i = 0; i < processes.Count; i++)
            if (!visited[i]) result.Add(BuildNode(i, 0));
        return result;

        ProcessTreeNode BuildNode(int idx, int depth)
        {
            visited[idx] = true;
            var kids = new List<ProcessTreeNode>();
            if (children[idx] is { } list)
                foreach (var c in list.OrderBy(c => processes[c].CreateTime))
                    if (!visited[c]) kids.Add(BuildNode(c, depth + 1));
            var p = processes[idx];
            bool missing = parents[idx] < 0 && p.ParentPid > 0 && p.ParentPid != p.Pid;
            return new ProcessTreeNode(p, kids, depth, missing);
        }
    }

    /// <summary>
    /// Para cada item, índice do pai válido na lista (-1 = raiz). Regras: PPID 0 ou igual ao próprio PID → raiz;
    /// pai ausente → raiz; pai criado DEPOIS do filho (PID reutilizado) → raiz.
    /// </summary>
    public static int[] ResolveParents(IReadOnlyList<(int Pid, int ParentPid, long CreateTime)> items)
    {
        var byPid = new Dictionary<int, int>(items.Count);
        for (int i = 0; i < items.Count; i++) byPid[items[i].Pid] = i;
        var result = new int[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var (pid, ppid, create) = items[i];
            result[i] = -1;
            if (ppid <= 0 || ppid == pid) continue;
            if (!byPid.TryGetValue(ppid, out int pi)) continue;
            long parentCreate = items[pi].CreateTime;
            if (parentCreate > 0 && create > 0 && parentCreate > create) continue; // PID do pai reutilizado
            result[i] = pi;
        }
        // Quebra ciclos: segue a cadeia; se voltar a um nó já visto no mesmo caminho, corta.
        for (int i = 0; i < items.Count; i++)
        {
            var path = new HashSet<int> { i };
            int cur = result[i];
            int prev = i;
            while (cur >= 0)
            {
                if (!path.Add(cur)) { result[prev] = -1; break; }
                prev = cur;
                cur = result[cur];
            }
        }
        return result;
    }

    /// <summary>
    /// Descendentes de um processo em pós-ordem (folhas primeiro — ordem segura para encerrar uma árvore),
    /// respeitando a regra de hora de criação. Não inclui a raiz.
    /// </summary>
    public static IReadOnlyList<RawProcessInfo> GetDescendants(IReadOnlyList<RawProcessInfo> processes, int rootPid, long rootCreateTime)
    {
        var parents = ResolveParents(processes.Select(p => (p.Pid, p.ParentPid, p.CreateTime)).ToList());
        int rootIdx = -1;
        for (int i = 0; i < processes.Count; i++)
            if (processes[i].Pid == rootPid && processes[i].CreateTime == rootCreateTime) { rootIdx = i; break; }
        if (rootIdx < 0) return Array.Empty<RawProcessInfo>();

        var children = new Dictionary<int, List<int>>();
        for (int i = 0; i < parents.Length; i++)
            if (parents[i] >= 0) (children.TryGetValue(parents[i], out var l) ? l : children[parents[i]] = new List<int>()).Add(i);

        var result = new List<RawProcessInfo>();
        var visited = new HashSet<int> { rootIdx };
        Visit(rootIdx);
        return result;

        void Visit(int idx)
        {
            if (!children.TryGetValue(idx, out var kids)) return;
            foreach (var k in kids)
            {
                if (!visited.Add(k)) continue;
                Visit(k);
                result.Add(processes[k]);
            }
        }
    }

    /// <summary>Lista plana (pré-ordem) para exibição com indentação.</summary>
    public static IEnumerable<ProcessTreeNode> Flatten(IEnumerable<ProcessTreeNode> roots)
    {
        var stack = new Stack<ProcessTreeNode>(roots.Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }
}
