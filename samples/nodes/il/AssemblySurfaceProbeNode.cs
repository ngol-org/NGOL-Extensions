using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// ロード済みアセンブリの規模を測る。
///
/// なぜ要るか:
///   ホットリロードでノードを復元するとき、Location を持つ全アセンブリに対して
///   GetTypes() を呼ぶ。この負荷はロード済みアセンブリ数と型数に比例する。
///   遅延ロードするアセンブリがある環境では、**セッションが進むほど重くなる**
///   --同じ操作なのに時間帯によって結果が変わる、という形で現れる。
///
///   それをこの数値で説明できるか確かめる。
///
/// measureGetTypes について:
///   既定 false。true にすると実際に GetTypes() を呼んで型数と所要時間を測るが、
///   **これはクラッシュ経路と同じ操作**である。ここではメインスレッドから呼ぶため
///   バックグラウンドスレッド起因の危険は避けられるが、無害である保証はない。
///   1本ずつログへ出してから呼ぶので、落ちた場合はログの最終行が犯人になる。
/// </summary>
[NodeType("ngol.il.assembly_surface", "IL", "Assembly Surface Probe",
    Version = "1.1.2",
    Description = "Report how many assemblies are loaded and what they are. Walking every assembly with GetTypes() is "
      + "what a hot reload does, so this shows how much that walk grows over a session. Assemblies whose GetTypes() "
      + "throws contribute no types, so their count and the reason are reported alongside.")]
[NodePort("measureGetTypes", PortDirection.Input, "boolean", Description = "true = actually call GetTypes() and measure the type count and elapsed time (default false). This is the same operation that has been seen to crash, so use it deliberately")]
[NodePort("assemblyCount", PortDirection.Output, "number", Description = "Assemblies currently loaded")]
[NodePort("scannedCount", PortDirection.Output, "number", Description = "How many of them GetTypes() would be called on: those that are not dynamic and have a Location")]
[NodePort("group_prefixes", PortDirection.Input, "string", Description = "Assembly name prefixes to count separately, comma separated. Empty = no breakdown")]
[NodePort("matchedCount", PortDirection.Output, "number", Description = "How many assemblies started with one of group_prefixes")]
[NodePort("failedCount", PortDirection.Output, "number", Description = "With measureGetTypes=true, how many GetTypes() calls threw. Their types are not in totalTypes")]
[NodePort("totalTypes", PortDirection.Output, "number", Description = "With measureGetTypes=true, the total type count. The failedCount assemblies are not included")]
[NodePort("elapsedMs", PortDirection.Output, "number", Description = "With measureGetTypes=true, how long all the GetTypes() calls took together")]
[NodePort("result", PortDirection.Output, "string", Description = "The breakdown, as a report")]
public sealed class AssemblySurfaceProbeNode : INode
{
    public void Execute(IExecutionContext ctx)
    {
        bool measure = ctx.GetPortValue("measureGetTypes") as bool? ?? false;
        var prefixes = ((ctx.GetPortValue("group_prefixes") as string) ?? "")
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

        var all = AppDomain.CurrentDomain.GetAssemblies();
        int scanned = 0, matched = 0, totalTypes = 0, failed = 0;
        double elapsed = 0;
        var heavy = new List<string>();
        var failures = new List<string>();

        foreach (var asm in all)
        {
            if (asm.IsDynamic) continue;
            string loc;
            try { loc = asm.Location; } catch { continue; }
            if (string.IsNullOrEmpty(loc)) continue;

            scanned++;
            var name = asm.GetName().Name ?? "";
            if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) matched++;

            if (measure)
            {
                // 落ちた場合に犯人が分かるよう、呼ぶ前に必ず残す。
                ctx.Logger.LogInfo($"[AsmSurface] GetTypes() -> {name}");
                var sw = Stopwatch.StartNew();
                int n = 0;
                // 失敗を黙って捨てると totalTypes が理由なく少なく出る。件数と理由を残す。
                try { n = asm.GetTypes().Length; }
                catch (Exception ex)
                {
                    n = -1;
                    failed++;
                    failures.Add($"{name}: {ex.GetType().Name} - {Reason(ex)}");
                }
                double ms = sw.Elapsed.TotalMilliseconds;
                elapsed += ms;
                if (n > 0) totalTypes += n;
                if (ms >= 5.0) heavy.Add($"{name}={n} types/{ms:F0}ms");
            }
        }

        var sb = new StringBuilder();
        sb.Append($"assemblies loaded={all.Length}\n");
        sb.Append($"GetTypes() would run on={scanned} (not dynamic and has a Location)");
        sb.Append(prefixes.Length > 0 ? $"  of which matching a prefix={matched}\n" : "\n");
        if (measure)
        {
            sb.Append($"total types={totalTypes}  GetTypes() total={elapsed:F1}ms");
            sb.Append(failed > 0 ? $"  {failed} of them threw and contributed no types\n" : "\n");
            if (heavy.Count > 0)
            {
                sb.Append("took 5ms or more:\n");
                foreach (var h in heavy) sb.Append("  ").Append(h).Append('\n');
            }
            if (failures.Count > 0)
            {
                sb.Append("GetTypes() threw on:\n");
                foreach (var f in failures) sb.Append("  ").Append(f).Append('\n');
            }
        }
        else
        {
            sb.Append("(measureGetTypes=false, so type counts and timings were not measured)\n");
        }

        ctx.SetPortValue("assemblyCount", all.Length);
        ctx.SetPortValue("scannedCount", scanned);
        ctx.SetPortValue("matchedCount", matched);
        ctx.SetPortValue("failedCount", failed);
        ctx.SetPortValue("totalTypes", totalTypes);
        ctx.SetPortValue("elapsedMs", elapsed);
        ctx.SetPortValue("result", sb.ToString());
    }

    /// <summary>
    /// 失敗の理由を 1 行にまとめる。
    /// 型解決の失敗（ReflectionTypeLoadException）は Message が定型文で、
    ///   どの型がどのアセンブリに見つからないかは LoaderExceptions 側にしか無い。
    ///   1 行目だけ取ると原因が消えるので、そちらを優先して使う。
    /// </summary>
    private static string Reason(Exception ex)
    {
        string text = ex.Message ?? "";

        if (ex is System.Reflection.ReflectionTypeLoadException rtle && rtle.LoaderExceptions != null)
        {
            foreach (var inner in rtle.LoaderExceptions)
            {
                if (inner == null || string.IsNullOrEmpty(inner.Message)) continue;
                text = inner.Message;
                break;
            }
        }

        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length <= 300 ? text : text.Substring(0, 300) + "...";
    }
}
