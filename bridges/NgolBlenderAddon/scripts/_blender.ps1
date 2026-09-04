#Requires -Version 5.1
# どの Blender を相手にするかを決める。配置・パッケージ作成・導入の 3 つが同じ問いを持つので
# 1 箇所に集めてある（scripts\_addon.py が Python 側で同じ役目を持っているのと対）。
#
# 版もインストール先も決め打ちにしない。
#    Blender は版ごとに別のフォルダへ入り、置き場所は利用者が選べる（ProgramFiles とは限らない）。
#    固定すると、その通りでない機械では黙って別の場所を指す。
#    配置側は「置けた」と表示して終わるため、症状は「入れたのに出ない」になり原因が見えない。
#
# 使う側:
#   . (Join-Path $PSScriptRoot "_blender.ps1")
#   $exe = Resolve-BlenderExe -Hint $BlenderExe
#   $ver = Get-BlenderUserVersion -Exe $exe        # "5.0"（利用者フォルダの名前）

# ここで Set-StrictMode を呼ばないこと。dot-source は呼び出し元のスコープで走るので、
#    読み込んだ側の設定まで変えてしまう。
#    道具は呼ぶ側の環境を書き換えない。

function Get-InstalledBlenders {
    <#
      入っている Blender を新しい順に返す（Version / Exe）。

      拾い方は 3 通りで、どれも置き場所を前提にしない:
        1) 動いているプロセス   ... 携帯版など、登録されない入れ方でも捕まる
        2) Windows の登録情報   ... インストーラが必ず書く。InstallLocation がそのまま答え
        3) よくある置き場       ... 1)2)が空だったときの最後の当て
    #>
    $found = @()

    foreach ($p in @(Get-Process -Name blender -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }   # 別ユーザーのプロセスは Path を読めない
        if ($path -and (Test-Path $path)) {
            $found += [PSCustomObject]@{ Version = (Get-Item $path).VersionInfo.FileVersion; Exe = $path }
        }
    }

    foreach ($root in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        foreach ($key in @(Get-ChildItem $root -ErrorAction SilentlyContinue)) {
            $info = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if (-not $info) { continue }
            $name = $info.PSObject.Properties['DisplayName']
            $loc  = $info.PSObject.Properties['InstallLocation']
            if (-not $name -or $name.Value -ne 'Blender' -or -not $loc -or -not $loc.Value) { continue }
            $exe = Join-Path $loc.Value 'blender.exe'
            if (-not (Test-Path $exe)) { continue }
            $ver = $info.PSObject.Properties['DisplayVersion']
            $found += [PSCustomObject]@{
                Version = $(if ($ver) { $ver.Value } else { (Get-Item $exe).VersionInfo.FileVersion }); Exe = $exe
            }
        }
    }

    if ($found.Count -eq 0) {
        foreach ($base in @("$env:ProgramFiles\Blender Foundation",
                            "${env:ProgramFiles(x86)}\Blender Foundation",
                            "$env:LOCALAPPDATA\Programs\Blender Foundation")) {
            if (-not (Test-Path $base)) { continue }
            foreach ($dir in @(Get-ChildItem $base -Directory -ErrorAction SilentlyContinue)) {
                $exe = Join-Path $dir.FullName 'blender.exe'
                if (Test-Path $exe) {
                    $found += [PSCustomObject]@{ Version = (Get-Item $exe).VersionInfo.FileVersion; Exe = $exe }
                }
            }
        }
    }

    # 同じ exe が複数の経路で挙がるので畳む
    $found |
        Group-Object Exe |
        ForEach-Object { $_.Group[0] } |
        Sort-Object { try { [version](($_.Version -replace '[^\d.].*$', '').TrimEnd('.')) } catch { [version]'0.0' } } -Descending
}

function Resolve-BlenderExe {
    <#
      使う blender.exe を決める。-Hint が渡されていればそれを検算するだけ。
      見つからないときは既定を組み立てて返さない--存在しないパスを返すと、
        呼んだ側が「起動に失敗した」と読むまで原因が分からなくなる。
    #>
    param([string]$Hint = "")

    if ($Hint) {
        if (Test-Path $Hint) { return $Hint }
        throw "blender.exe が見つかりません: $Hint"
    }

    $all = @(Get-InstalledBlenders)
    if ($all.Count -eq 0) {
        throw "Blender が見つかりません。-BlenderExe で blender.exe の場所を指定してください"
    }
    if ($all.Count -gt 1) {
        Write-Host ("  Blender  : {0}  ({1} 件のうち最新。-BlenderExe で選べます: {2})" -f
                    $all[0].Version, $all.Count,
                    (($all | ForEach-Object { $_.Version }) -join ", ")) -ForegroundColor DarkGray
    }
    return $all[0].Exe
}

function Get-BlenderUserVersion {
    <#
      利用者フォルダの名前（%APPDATA%\Blender Foundation\Blender\<ここ>）を返す。
      実行ファイルの版は "5.0.0" だがフォルダは "5.0"。major.minor まで落とす。
    #>
    param([Parameter(Mandatory = $true)][string]$Exe)

    $raw = (Get-Item $Exe).VersionInfo.FileVersion
    if (-not $raw) { throw "blender.exe から版を読めません: $Exe" }
    $parts = ($raw -replace '[^\d.].*$', '').Split('.')
    if ($parts.Count -lt 2) { throw "blender.exe の版が読めない形です: $raw" }
    return ($parts[0] + '.' + $parts[1])
}

function Get-BlenderUserRoot {
    <#
      利用者フォルダ（config / scripts / extensions が並ぶところ）を返す。

      %APPDATA% の下とは限らない。Blender は次の順で決める:
          BLENDER_USER_RESOURCES があればそこが全体の置き換え
          Windows ストア版・携帯版は %APPDATA% を使わない
        決め打ちにすると、そういう機械で黙って別の場所を指す。

      -Ask を付けると Blender 自身に聞く（bpy.utils.resource_path('USER')）。
      環境変数も携帯版もストア版も込みで正しいが、起動するぶん数秒かかる。
        既に Blender を起動する用のスクリプトでは付けてよい。配置だけの用途では重い。
    #>
    param([string]$Exe = "", [string]$Version = "", [switch]$Ask)

    if ($Ask -and $Exe) {
        # 答えを前後の印で挟む。Blender は版や統計を同じ流れへ書くので、
        #    「印から行末まで」で取ると続いた文字までくっついてくる。
        $expr = "import bpy,sys; sys.stdout.write('<NGOL_ROOT>' + bpy.utils.resource_path('USER') + '</NGOL_ROOT>')"
        $out = (& $Exe -b --factory-startup --python-expr $expr 2>$null) -join "`n"
        if ($out -match '<NGOL_ROOT>(.*?)</NGOL_ROOT>') { return $Matches[1].Trim() }
        Write-Host "  Blender に利用者フォルダを聞けませんでした。既定の場所で続けます" -ForegroundColor DarkYellow
    }

    if ($env:BLENDER_USER_RESOURCES) { return $env:BLENDER_USER_RESOURCES }

    if (-not $Version) {
        if (-not $Exe) { throw "利用者フォルダを決めるには -Version か -Exe が要ります" }
        $Version = Get-BlenderUserVersion -Exe $Exe
    }
    return (Join-Path $env:APPDATA "Blender Foundation\Blender\$Version")
}

function Get-BlenderExtensionsDir {
    <#
      拡張の置き場を返す。
      BLENDER_USER_EXTENSIONS は BLENDER_USER_RESOURCES より優先される（Blender の決まり）。
    #>
    param([string]$Exe = "", [string]$Version = "", [switch]$Ask)

    if ($env:BLENDER_USER_EXTENSIONS) { return $env:BLENDER_USER_EXTENSIONS }
    return (Join-Path (Get-BlenderUserRoot -Exe $Exe -Version $Version -Ask:$Ask) 'extensions')
}
