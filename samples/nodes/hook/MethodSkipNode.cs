using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using NodeGraphModLab.NodeAPI;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 指定したメソッドを**丸ごと実行させない**診断ノード。対象のソースには一切手を入れない。
///
/// 実装は IL 書き換え（`ILHook`）で、本体の先頭に「数える -> 戻り値の既定値を積む -> ret」を差し込む。
/// メソッド単位の detour ではなく IL 書き換えを使う理由は、
/// **対象を実行時に名前で見つけるためシグネチャが分からない**こと。
/// シグネチャ一致のデリゲートを要求する方式は、この用途には使えない。
///
/// なぜ「時間を計る」ではなく「止める」のか:
///   時間を計る方法では、GPU の完了待ちのように**誰の帳簿に乗るか分からない待ち**を
///   正しく割り振れない。止めてみて全体が軽くなるかを見れば、因果がそのまま出る。
///   「重いはず」という推測を挟まずに済む。
///
/// 負荷調査のほかに、**外から値を書き込む間だけアプリ側の更新処理を黙らせる**用途にも使う
/// （例: ブレンドシェイプの重みを毎フレーム上書きしてくる適用処理を止める）。
///
/// **戻り値のあるメソッドは既定で拒否する。** 止めると戻り値が既定値のまま返り、
///   呼び出し元が壊れる。必要なら `allowNonVoid` を立てるが、
///   何が返るかを承知したうえで使うこと。
///
/// プロパティのアクセサ等（IsSpecialName）は対象にしない。
///   環境によっては危険な方式でパッチされ、プロセスごと落ちることがある
///   （ClassMethodTimingNode の注記と同じ理由）。
///
/// **設置できたことは効いた証拠にならない。** 解除時に「飛ばした回数」を出すので、
///   0 のままなら対象が呼ばれていないかパッチが空振りしている。必ず数で確かめること。
/// </summary>
[NodeType("ngol.hook.managed_skip", "Hook", "Skip Managed Method",
    Version = "1.0.2",
    Description = "Skip a method entirely by rewriting its IL, without touching the target's source. Use it to locate load by turning things off (stronger than timing), or to silence an update loop while writing values from outside. Reports how many times it actually skipped when released.")]
[NodePort("typeName", PortDirection.Input, "string", IsRequired = true,
    Description = "Target class. Simple name, or a dotted full name to disambiguate same-named classes")]
[NodePort("methodNames", PortDirection.Input, "string", IsRequired = true,
    Description = "Comma-separated method names to skip")]
[NodePort("enabled", PortDirection.Input, "boolean",
    Description = "true (default) = install the skip. false = release it and report the skip counts")]
[NodePort("allowNonVoid", PortDirection.Input, "boolean",
    Description = "Also skip methods that return a value. Default false, because the caller receives a default value and may break")]
[NodePort("result", PortDirection.Output, "string",
    Description = "What was patched, or the skip counts when released")]
public sealed class MethodSkipNode : INode
{
    // 飛ばした回数。効いているかを数で確かめる（ログに出ないと空振りに気付けない）
    static readonly Dictionary<string, int> s_hits = new Dictionary<string, int>();
    static readonly List<ILHook> s_hooks = new List<ILHook>();

    /// <summary>差し込んだ IL から呼ばれる。どのメソッドを飛ばしたかを数える。</summary>
    public static void CountSkip(string key)
    {
        lock (s_hits) s_hits[key] = s_hits.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    /// <summary>
    /// 本体の先頭に「数える -> 戻り値の既定値 -> ret」を差し込む。
    /// 元の命令列は残るが到達しない。
    /// </summary>
    static void EmitSkip(ILContext il, string key, Type returnType)
    {
        var c = new ILCursor(il);
        c.Goto(0);

        c.Emit(OpCodes.Ldstr, key);
        c.EmitDelegate<Action<string>>(CountSkip);

        if (returnType != typeof(void))
        {
            if (returnType.IsValueType)
            {
                // 値型は ldnull できない。ローカルを 0 初期化して積む。
                var tref = il.Method.Module.ImportReference(returnType);
                var local = new VariableDefinition(tref);
                il.Body.Variables.Add(local);
                il.Body.InitLocals = true;
                c.Emit(OpCodes.Ldloca, local);
                c.Emit(OpCodes.Initobj, tref);
                c.Emit(OpCodes.Ldloc, local);
            }
            else
            {
                c.Emit(OpCodes.Ldnull);
            }
        }

        c.Emit(OpCodes.Ret);
    }

    public void Execute(IExecutionContext ctx)
    {
        var typeName = (ctx.GetPortValue("typeName") as string ?? "").Trim();
        var names = (ctx.GetPortValue("methodNames") as string ?? "").Trim();
        var enabled = ctx.GetPortValue("enabled") as bool? ?? true;
        var allowNonVoid = ctx.GetPortValue("allowNonVoid") as bool? ?? false;

        var sb = new StringBuilder();

        if (!enabled)
        {
            foreach (var h in s_hooks)
            {
                try { h.Dispose(); }
                catch (Exception ex) { sb.AppendLine("Dispose threw: " + ex.Message); }
            }
            s_hooks.Clear();
            sb.Append("Released. ");
            lock (s_hits)
            {
                if (s_hits.Count > 0)
                {
                    sb.Append("skipped: ");
                    foreach (var kv in s_hits) sb.Append($"{kv.Key}={kv.Value} ");
                }
                else
                {
                    sb.Append("*** never skipped - the target was not called, or the patch missed");
                }
                s_hits.Clear();
            }
            ctx.Logger.LogInfo("[MethodSkip] " + sb);
            ctx.SetPortValue("result", sb.ToString());
            return;
        }

        if (typeName.Length == 0 || names.Length == 0)
        {
            ctx.SetPortValue("result", "typeName and methodNames are required");
            return;
        }

        // ホットリロードすると同名の型が世代の数だけ存在する。最初に見つかったものを掴むと、
        //   **一度も実行されていない世代**にパッチを当てて「設置できたのに何も起きない」になる。
        //   全部見つけて全部に当てる。どれが動いているかは飛ばした回数で分かる。
        // 単純名だけで探すと同名の別クラスを掴む（`Camera` のように複数のライブラリに
        //   同じ名前があることは珍しくない）。ドットを含む指定なら完全名で照合する。
        var byFullName = typeName.Contains(".");
        var targets = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types)
                if (byFullName ? t.FullName == typeName : t.Name == typeName) targets.Add(t);
        }
        if (targets.Count == 0)
        {
            var msg = $"class {typeName} not found";
            ctx.Logger.LogWarning("[MethodSkip] " + msg);
            ctx.SetPortValue("result", msg);
            return;
        }
        sb.AppendLine($"{targets.Count} generation(s) of the type - patching all of them");

        lock (s_hits) s_hits.Clear();
        var patched = 0;
        for (var gen = 0; gen < targets.Count; gen++)
        {
            var target = targets[gen];
            foreach (var raw in names.Split(','))
            {
                var name = raw.Trim();
                if (name.Length == 0) continue;

                var m = target.GetMethod(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (m == null) { sb.AppendLine($"[{gen}] {name}: not found"); continue; }
                if (m.IsSpecialName) { sb.AppendLine($"[{gen}] {name}: accessor, skipped"); continue; }
                if (m.ReturnType != typeof(void) && !allowNonVoid)
                {
                    sb.AppendLine($"[{gen}] {name}: returns {m.ReturnType.Name}, refused");
                    continue;
                }

                var key = m.Name;
                var ret = m.ReturnType;
                try
                {
                    s_hooks.Add(new ILHook(m, il => EmitSkip(il, key, ret)));
                    patched++;
                }
                catch (Exception ex) { sb.AppendLine($"[{gen}] {name}: hook failed {ex.Message}"); }
            }
        }

        sb.Append($"-> {patched} site(s) will be skipped ({targets[0].FullName})");
        ctx.Logger.LogInfo("[MethodSkip] " + sb);
        ctx.SetPortValue("result", sb.ToString());
    }
}
