#pragma once

#include <string>

// ホストの操作を 1 か所に集める。要求も答えも JSON。
//
// 操作ごとに export を生やさない。JSON の読み書きはホスト自身が持っている
// (obs_data) ので外部ライブラリが要らず、操作を足しても NGOL 側の宣言が動かない。
namespace ObsOps {

// UI スレッドで処理する要求。requestJson の "op" で分ける。
std::string HandleOnUiThread(const std::string& requestJson);

// 描画スレッドの錠を取る要求。UI スレッドへは渡さない。
std::string HandleCapture(const std::string& requestJson);

// 直前に控えた画素を引き取る。out が null なら要る長さだけ返す。
int TakeFrame(unsigned char* out, int outLen, int* width, int* height, int* pitch);

// ホストからの通知を積む。積むだけで、ここからホストを操作しない。
void PushEvent(const char* name);

}
