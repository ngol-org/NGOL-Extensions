using System;
using System.Collections.Generic;
using System.Linq;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// 値スキャンの候補集合を、スキャンとスキャンの間だけ保持する。
///
/// 保存先は AppDomain であって永続ストアではない。
///   候補は絶対アドレスの集合なので、対象プロセスが終わった時点で意味を失う。
///   ディスクへ残しても次回使えないのに、書き込み量だけが積み上がる--
///   永続ストアへ大量に貯めると起動できなくなる。
///   AppDomain ならノードのホットリロードはまたいで生き残り、プロセス終了で必ず消える。
///
/// AppDomain に入れてよいのはフレームワークの型だけ。
///   独自の型はホットリロードで別の型になり、取り出したときにキャストできなくなる。
///   そのため long[] / double[] / string / string[] に分解して持つ。
///
/// セッションは MaxSessions 個までで、古いものから自動的に捨てる。
///   利用者が明示的に片付けなくても、走査を繰り返すだけで際限なく増えることはない。
/// </summary>
internal static class NgolScanSession
{
    private const string KeyPrefix = "NgolValueScan_";
    private const string IndexKey = "NgolValueScan_sessions";
    private const int MaxSessions = 4;

    public static void Save(string id, string valueType, long[] addresses, double[] values)
    {
        var domain = AppDomain.CurrentDomain;
        domain.SetData(KeyPrefix + id + "_type", valueType);
        domain.SetData(KeyPrefix + id + "_addrs", addresses);
        domain.SetData(KeyPrefix + id + "_values", values);

        var order = ListSessions().Where(s => s != id).ToList();
        order.Add(id);
        while (order.Count > MaxSessions)
        {
            Drop(order[0]);
            order.RemoveAt(0);
        }
        domain.SetData(IndexKey, order.ToArray());
    }

    public static bool TryLoad(string id, out string valueType, out long[] addresses, out double[] values)
    {
        var domain = AppDomain.CurrentDomain;
        valueType = domain.GetData(KeyPrefix + id + "_type") as string;
        addresses = domain.GetData(KeyPrefix + id + "_addrs") as long[];
        values = domain.GetData(KeyPrefix + id + "_values") as double[];
        return !string.IsNullOrEmpty(valueType) && addresses != null && values != null
            && addresses.Length == values.Length;
    }

    public static string[] ListSessions()
        => AppDomain.CurrentDomain.GetData(IndexKey) as string[] ?? Array.Empty<string>();

    private static void Drop(string id)
    {
        var domain = AppDomain.CurrentDomain;
        domain.SetData(KeyPrefix + id + "_type", null);
        domain.SetData(KeyPrefix + id + "_addrs", null);
        domain.SetData(KeyPrefix + id + "_values", null);
    }
}
