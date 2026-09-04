using System;

namespace NodeGraphModLab.CustomNodes;

/// <summary>
/// int32/int64/float/double とバイト列の相互変換。値スキャン・型付き読み書きの各ノードで共有する。
/// </summary>
internal static class NgolValueCodec
{
    public static int SizeOf(string type) => type switch
    {
        "int32" => 4,
        "int64" => 8,
        "float" => 4,
        "double" => 8,
        _ => 0,
    };

    /// <summary>
    /// 同じ幅の整数型・浮動小数点型の名前。
    /// 走査する側から見ると 4 バイトの整数と float は区別がつかない--同じバイト列を
    ///   両方で解釈して並べられるように、幅から相方の型を引けるようにしている。
    /// </summary>
    public static string IntTypeOfSize(int size) => size == 8 ? "int64" : "int32";
    public static string FloatTypeOfSize(int size) => size == 8 ? "double" : "float";

    public static double Decode(string type, byte[] bytes) => DecodeAt(type, bytes, 0);

    // オフセット直接読み取り。scan 系のホットパスで、候補ごとの部分配列コピーを避けるために使う。
    public static double DecodeAt(string type, byte[] buf, int offset) => type switch
    {
        "int32" => BitConverter.ToInt32(buf, offset),
        "int64" => BitConverter.ToInt64(buf, offset),
        "float" => BitConverter.ToSingle(buf, offset),
        "double" => BitConverter.ToDouble(buf, offset),
        _ => 0,
    };

    public static byte[] Encode(string type, double value) => type switch
    {
        "int32" => BitConverter.GetBytes((int)value),
        "int64" => BitConverter.GetBytes((long)value),
        "float" => BitConverter.GetBytes((float)value),
        "double" => BitConverter.GetBytes(value),
        _ => Array.Empty<byte>(),
    };

    // 浮動小数点は誤差があるため厳密一致でなく許容差で比べる。整数は完全一致。
    public static bool MatchesAt(string type, byte[] buf, int offset, double target, double tolerance)
    {
        var v = DecodeAt(type, buf, offset);
        if (type == "float" || type == "double") return Math.Abs(v - target) <= tolerance;
        return v == target;
    }
}
