using System;

namespace NgolExt.NativeHook;

/// <summary>
/// ngol_native.dll が提供するネイティブフック・メモリ読書き・スタックトレース機能のサービスインターフェース。
/// ctx.GetExtensionService&lt;INativeHookService&gt;() で取得する。
/// ngol.ext.native-hook Extension がロードされていない場合は null が返る。
/// </summary>
public interface INativeHookService
{
    // -- フック管理 ------------------------------------------------------------

    /// <summary>
    /// 対象の先頭を差し替えて、呼ばれるたびに回数と引数を控えるようにする。
    /// 設置した時点では元関数を呼ぶ（対象の挙動は変わらない）。止めたい場合だけ
    /// SetCallOriginal(hook, false) を呼ぶ。
    /// </summary>
    bool Install(IntPtr pTarget, out IntPtr hook);

    /// <summary>
    /// XMM(浮動小数点)対応版フック設置。floatSlotMaskのbit i(0-3)が1なら、レジスタ渡し
    /// スロットi はXMMレジスタ渡し(float/double)として捕捉・転送される（0-15、既定は全ビット0＝Install相当）。
    /// 既存のInstall（64個）とは別枠の小規模プール（16個）を使う。
    /// SetExtraStackArgsとの併用は非対応（count>0を設定するとエラーになる）。
    /// </summary>
    bool InstallTyped(IntPtr pTarget, int floatSlotMask, out IntPtr hook);
    bool Uninstall(IntPtr hook);
    void UninstallAll();
    void Read(IntPtr hook, out long count, out long a0, out long a1, out long a2, out long a3);
    void ResetCount(IntPtr hook);
    bool IsActive(IntPtr hook);
    bool SetCallOriginal(IntPtr hook, bool callOriginal);

    /// <summary>
    /// 元関数を呼ばない設定のときに、呼び出し元へ返す値を決める。
    /// 設定しなければ 0 が返る（設置時に初期化される）。
    /// 整数・ポインタとして返る値だけが対象。浮動小数点は別のレジスタで返るため反映されない。
    /// 元関数を呼ぶ設定では使われない（そのときは元関数が返した値がそのまま渡る）。
    /// </summary>
    bool SetReturnValue(IntPtr hook, long value);

    IntPtr GetTrampoline(IntPtr hook);

    /// <summary>
    /// レジスタ4個(a0-a3)を超える追加引数の個数(0-8)を設定する。x64呼び出し規約ではこの分はスタック経由で渡される。
    /// call_original=true 時、元関数への転送をこの個数に応じた正しい引数個数で行うようになる。
    /// 呼ばなければ従来通り0（4引数のみ転送）で、5引数以上を持つ関数をフックすると元関数が不定値を受け取る（既知の制約）。
    /// </summary>
    bool SetExtraStackArgs(IntPtr hook, int count);

    /// <summary>
    /// 直近フック発火時に捕捉した追加引数（第5引数以降、最大8個）を読み取る。
    /// SetExtraStackArgs で設定した個数分のみ有効な値が入る。
    /// </summary>
    long[] ReadExtra(IntPtr hook, int count);

    /// <summary>
    /// 直近フック発火時の、呼び出し元へ戻る番地。まだ一度も通っていなければ 0。
    /// モジュールの載り位置を引けばモジュール名 + RVA になり、逆アセンブルや参照元の検索へ渡せる。
    /// ここで返るのは 1 段目だけ。それ以上は呼ぶ側で辿る（巻き戻しは .pdata が要り
    /// 動的生成コードで止まる／走査は情報が要らないが偽陽性が出る）。
    /// カーネル側まで要るなら ETW（WPR）。
    /// </summary>
    long ReadReturnAddress(IntPtr hook);

    /// <summary>
    /// フック発火時（統計記録の直後・callOriginal分岐の前）に同期呼び出しされるコールバックを登録する。
    /// callback に null を渡すと解除。
    /// コールバックはフック対象を呼んだスレッド上で同期実行される（UIスレッドとは限らない）。
    /// コールバック内で例外を投げてはならない（実装側で必ず捕捉し、ネイティブ境界を越えて伝播させない）。
    /// コールバック内でフック対象自身（または同じ内部状態を操作する別関数）を呼ぶと、
    /// 再入により無限再帰や状態不整合を起こす可能性があるため注意すること。
    /// </summary>
    bool SetManagedCallback(IntPtr hook, Action<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>? callback);

    // -- 発火のたびに 1 件ずつ書く貸し先 ---------------------------------------

    /// <summary>
    /// 1 件のバイト数。置き場の寸法を出すのと、書式のずれを見張るために使う。
    /// frames は 1 件に足す呼び出し元の連なりの段数（0-64）。
    /// </summary>
    int RecordSize(int frames);

    /// <summary>
    /// 置き場を貸す。貸している間、発火のたびに 1 件書かれる。端まで来たら先頭へ戻る。
    /// buffer が IntPtr.Zero または capacity が 0 なら貸すのをやめる。
    /// 置き場は呼ぶ側が確保する。大きさは capacity * RecordSize(frames) バイトで、capacity は 2 の冪。
    /// 1 件は決まった long 6 個 [seq, returnAddress, a0, a1, a2, a3] と、frames 個の連なり。
    /// seq は 1 始まりで最後に書かれ、0 は書きかけの印。読む側は seq を前後 2 回読み、
    /// 両方が目当ての番号のときだけ採ること。
    /// firstSeq にはこれ以降に書かれる最初の番号が返る（貸す前の発火は記録されない）。
    /// frames を 0 より大きくすると 1 件あたりの費用が 2 桁上がる。既定は 0 のまま使うこと。
    /// 返す順序を守ること: 貸すのをやめる -> Uninstall -> 呼び出しが止まったのを確かめる -> 解放。
    /// 直後に解放すると、書きかけの 1 件が解放済みの番地へ落ちる。
    /// </summary>
    bool SetRecordBuffer(IntPtr hook, IntPtr buffer, int capacity, int frames, out long firstSeq);

    // -- メモリ操作 -----------------------------------------------------------
    bool ReadQWORD(IntPtr pAddr, out long value);
    bool ReadDWORD(IntPtr pAddr, out uint value);
    bool ReadBytes(IntPtr pAddr, byte[] buf, UIntPtr len);
    bool IsReadable(IntPtr pAddr, UIntPtr len);
    bool WriteQWORD(IntPtr pAddr, long value);
    bool WriteBytes(IntPtr pAddr, byte[] buf, UIntPtr len);

    // -- デバッグ -------------------------------------------------------------
    uint StackTrace(IntPtr[] frames, uint maxFrames);

    /// <summary>
    /// pObj を「先頭がクラス情報へのポインタ」という形のオブジェクトとみなし、
    /// そのクラス名・名前空間の取得を試みる。当てはまらない形なら false を返す。
    /// SEH で保護されているため、pObj が無効なポインタ（bool/enum値・コードポインタ等）でも
    /// クラッシュせず false を返す。誤ラベリングの心配はない（成功時のみ意味のある値が入る）。
    /// </summary>
    bool TryGetKlassName(IntPtr pObj, out string className, out string classNamespace);

    // -- エラー取得 -----------------------------------------------------------
    string GetLastError();
}
