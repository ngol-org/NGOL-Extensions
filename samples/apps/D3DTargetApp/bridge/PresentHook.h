#pragma once

// アプリの D3D11 スワップチェーンの Present（画面更新イベント）をフックする。
// アプリはスワップチェーンを渡さないので、ダミーを作って IDXGISwapChain の vtable を取り、
// Present（slot 8）の関数ポインタを差し替える。vtable は dxgi のクラス共有なので、
// アプリのスワップチェーンの Present もこのフックを通る。
//
// onPresent は毎フレーム、アプリのレンダースレッドで呼ばれる（重い処理はしない）。

bool PresentHook_Install(void (*onPresent)());
void PresentHook_Remove();
