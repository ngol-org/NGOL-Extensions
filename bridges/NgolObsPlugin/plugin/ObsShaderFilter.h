#pragma once

#include <string>

struct obs_source;
typedef struct obs_source obs_source_t;

// 効果の計算式を文字列で受け取り、ホストが毎フレーム GPU で走らせる種別。
//
// これまでの道（素材を書き出して上へ載せる）は下地に手を付けられない。
// こちらは下地そのものを読んで書き換えるので、色を抜く・歪ませる・輪郭を出す、
// といったものが作れる。渡すのは文字列 1 つで、ファイルを作らない。
namespace ObsShaderFilter {

// ホストへ種別を差し出す。obs_module_load から 1 度だけ呼ぶ。
void Register();

// 受け取ったばかりの設定で、その場で作り直す。
// ホストは映像のソースへの更新を次の描画まで後回しにするため
// (obs-source.c の obs_source_update)、これを呼ばないと
// 直後に読める状態が 1 手古いままになる。このフィルタでなければ false。
bool ApplyNow(obs_source_t* filter);

// 直前のコンパイルで何が起きたかを読む。
// 計算式を書く側はこれが返らないと直しようがないので、応答へ必ず載せる。
// このフィルタでなければ false。
bool Status(obs_source_t* filter, std::string& error, int& preambleLines);

}
