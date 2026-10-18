# 一键部署：把仓库里"该进游戏"的那几样同步到本地 mod 目录
#
# 为什么要有这个脚本：本仓库是**开发树**，游戏读的是另一个目录
# （B:\SteamLibrary\...\Mods\【CA】数字存储-Digital Storage）。以前只手工拷 DLL，
# 结果 2026-10-03 出现"改了 Keyed 但没拷 → 新键显示成 DS_PowerMultiplier 原始键名"的线上事故。
#
# 规则：
#   · 只同步**运行时要的文件**：Assemblies / Defs / Languages / Patches / About / Textures
#   · 逐文件 SHA256 校验，不一致就列出
#   · 游戏在跑就拒绝（Windows 会锁 DLL，拷一半更糟）
#   · 不删目标目录里的多余文件（workshop.vdf、0Harmony.dll 之类由人工决定）
#
# 用法：pwsh -File Tools\deploy.ps1 [-WhatIf]

param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$dest = 'B:\SteamLibrary\steamapps\common\RimWorld\Mods\【CA】数字存储-Digital Storage'

if (-not (Test-Path $dest)) { throw "找不到目标 mod 目录：$dest" }

# B22 / 审计 2026-10-17：0Harmony.dll 绝不能随包发布。
# Harmony 由 HarmonyMod 本体分发；Assemblies\ 下的 dll 会被 ModAssemblyHandler **全部**加载，
# 带一份就是二次加载（版本错配、静态状态双份，崩得很随机）。
# 编译期引用已经挪到 Source\refs\，这里再兜一道，谁把它拷回 Assemblies\ 都会当场被拦下。
$strayHarmony = Join-Path $repo 'Assemblies\0Harmony.dll'
if (Test-Path $strayHarmony) {
    throw "Assemblies\0Harmony.dll 存在 —— 它不能进发布目录（见 Source\refs\ 与 About.xml 的依赖声明）。删掉它再部署。"
}

$running = Get-Process RimWorldWin64 -ErrorAction SilentlyContinue
if ($running) {
    throw "RimWorld 正在运行（PID $($running.Id -join ',')）—— 先关掉游戏再部署（DLL 被锁着，拷一半会留下坏状态）。"
}

# 目录名 → 同步方式（整目录）
$dirs = @('Defs', 'Languages', 'Patches', 'Textures', 'About')
# 单文件
$files = @(
    'Assemblies\DigitalStorage.dll',
    'Assemblies\DigitalStorage.pdb'
)

$copied = 0
$same = 0
$missing = New-Object System.Collections.Generic.List[string]

function Sync-File([string]$rel) {
    $src = Join-Path $repo $rel
    $dst = Join-Path $dest $rel
    if (-not (Test-Path $src)) { $script:missing.Add($rel); return }

    $a = (Get-FileHash $src -Algorithm SHA256).Hash
    if (Test-Path $dst) {
        $b = (Get-FileHash $dst -Algorithm SHA256).Hash
        if ($a -eq $b) { $script:same++; return }
    }

    if ($WhatIf) {
        Write-Host "  [WhatIf] 需要复制: $rel"
    } else {
        $dir = Split-Path -Parent $dst
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item $src $dst -Force
        $b = (Get-FileHash $dst -Algorithm SHA256).Hash
        if ($a -ne $b) { throw "复制后校验失败：$rel" }
        Write-Host "  复制: $rel"
    }
    $script:copied++
}

Write-Host "部署：$repo"
Write-Host "  →   $dest"

foreach ($d in $dirs) {
    $root = Join-Path $repo $d
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem $root -Recurse -File | ForEach-Object {
        Sync-File $_.FullName.Substring($repo.Length + 1)
    }
}
foreach ($f in $files) { Sync-File $f }

Write-Host ""
Write-Host "结果：复制 $copied 个 / 已一致 $same 个 / 仓库缺失 $($missing.Count) 个"
if ($missing.Count -gt 0) { Write-Host "仓库里没有（源文件漏了？）：$($missing -join ', ')" }
if ($WhatIf) { Write-Host "（WhatIf：什么都没写）" }
Write-Host ""
Write-Host "提醒：游戏读的是上面那个目录。Workshop 发布还要另外把 contentfolder 指向它，"
Write-Host "      并把 workshop.vdf 的 description 与 Docs\工坊介绍-线上正文.bbcode 对齐。"
