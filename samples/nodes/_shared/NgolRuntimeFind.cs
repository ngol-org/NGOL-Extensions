using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 実行文脈から参照をたどって、いま動いている NGOL の本体を探す。
///
/// 本体を指す公開された入口が無いため、これまではホスト側の保持者を名前で
/// 当てにいっていた。保持者は静的フィールドだったり静的プロパティだったり
/// ウィンドウのインスタンスフィールドだったりするので、名前の一覧では
/// 届かないホストが必ず残る。インスタンスに持つホストは名前を足しても届かない。
///
/// 実行文脈を作ったのは本体なので、参照をたどれば保持者を知らなくてよい。
/// たどる側は道順を持たない。配線が変わっても、道が在れば見つかる。
///
/// 読むのはインスタンスフィールドだけ。静的フィールドを読むとその型の
/// 初期化子が走り、無関係な型の初期化を生きたホストで起こすことになる。
/// プロパティも呼ばない。同じ理由で、副作用のある経路を踏まないため。
/// </summary>
internal static class NgolRuntimeFind
{
    internal const string RuntimeTypeName = "NodeGraphModLab.NgolRuntime";

    // 実測では 4 ホストとも深さ 6・数千個で尽きた。上限はその先を止めるためのもので、
    // 通常の探索を切るためのものではない。
    private const int MaxDepth = 12;
    private const int MaxVisited = 30000;

    // 1 本の配列に付き見る要素の数。長い配列を丸ごと辿ると、答えに近づかないまま数だけ増える。
    private const int MaxArrayItems = 512;

    internal struct Result
    {
        public object Runtime;      // null なら見つからなかった
        public int Visited;
        public int Depth;           // 見つけた深さ。見つからなければ -1
        public bool Exhausted;      // 上限ではなく、たどる先が尽きた
        public double ElapsedMs;

        /// <summary>
        /// 見つからなかったときに、何をどこまで見たのかを言う。
        /// 上限で切ったのか本当に届かないのかが分かれないと、次の一手が決まらない。
        /// </summary>
        public string Explain()
        {
            if (Runtime != null)
                return "found the running NGOL " + Depth + " references from the execution context"
                     + " (" + Visited + " objects, " + ElapsedMs.ToString("0.##") + " ms)";

            return Exhausted
                ? "the running NGOL is not reachable from the execution context by references"
                  + " (" + Visited + " objects, every one of them looked at)"
                : "the running NGOL was not found within the limits"
                  + " (" + Visited + " objects, the walk was cut short before it ran out)";
        }
    }

    private sealed class ByReference : IEqualityComparer<object>
    {
        public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
    }

    /// <summary>
    /// start から参照をたどって本体を探す。start には実行文脈をそのまま渡す。
    /// 同じプロセスに本体が 2 つあるときは、渡した文脈から届くほうが返る。
    /// </summary>
    internal static Result Find(object start)
    {
        var result = new Result { Depth = -1 };
        if (start == null) { result.Exhausted = true; return result; }

        var watch = Stopwatch.StartNew();
        var seen = new HashSet<object>(new ByReference());
        var queue = new Queue<Step>();
        queue.Enqueue(new Step(start, 0));
        seen.Add(start);

        while (queue.Count > 0 && result.Visited < MaxVisited)
        {
            var step = queue.Dequeue();
            result.Visited++;

            var type = step.Value.GetType();
            if (type.FullName == RuntimeTypeName)
            {
                result.Runtime = step.Value;
                result.Depth = step.Depth;
                break;
            }
            if (step.Depth >= MaxDepth) continue;

            foreach (var child in Children(step.Value, type))
            {
                var childType = child.GetType();
                if (childType == typeof(string) || childType.IsPrimitive) continue;
                if (childType.FullName == RuntimeTypeName)
                {
                    result.Runtime = child;
                    result.Depth = step.Depth + 1;
                    queue.Clear();
                    break;
                }
                if (seen.Add(child)) queue.Enqueue(new Step(child, step.Depth + 1));
            }
            if (result.Runtime != null) break;
        }

        result.Exhausted = result.Runtime == null && queue.Count == 0;
        result.ElapsedMs = watch.Elapsed.TotalMilliseconds;
        return result;
    }

    private struct Step
    {
        public readonly object Value;
        public readonly int Depth;
        public Step(object value, int depth) { Value = value; Depth = depth; }
    }

    private static IEnumerable<object> Children(object value, Type type)
    {
        var array = value as Array;
        if (array != null)
        {
            if (array.Rank == 1 && !type.GetElementType().IsPrimitive)
            {
                int count = array.Length < MaxArrayItems ? array.Length : MaxArrayItems;
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try { item = array.GetValue(i); } catch { }
                    if (item != null) yield return item;
                }
            }
            yield break;
        }

        // 継承の段ごとに宣言されたものだけを取る。GetFields は private を継承元から返さない。
        for (var walk = type; walk != null && walk != typeof(object); walk = walk.BaseType)
        {
            FieldInfo[] fields;
            try
            {
                fields = walk.GetFields(BindingFlags.Instance | BindingFlags.Public
                                      | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch { yield break; }

            foreach (var field in fields)
            {
                if (field.FieldType.IsPrimitive || field.FieldType == typeof(string)) continue;
                object item = null;
                try { item = field.GetValue(value); } catch { continue; }
                if (item != null) yield return item;
            }
        }
    }
}
