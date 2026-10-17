# 把主仓（kankejiang/CatClawVideo）克隆里的弹幕三件套同步到本仓库快照目录。
# 本仓库（CI 出包专用）里的 CatClawVideo/CatClawVideo.Core/Services/*.cs 是拷贝快照，
# 主仓逻辑更新后跑一次本脚本，再 commit & push。
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File sync-from-main.ps1 -MainRepo ..\CatClawVideo

param(
    [string]$MainRepo = "..\CatClawVideo"
)

$dest = Join-Path $PSScriptRoot "CatClawVideo\CatClawVideo.Core\Services"
$files = @("DanmuSearchService.cs", "DanmuParser.cs", "UserAgents.cs")

if (-not (Test-Path $dest)) {
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
}

$fail = $false
foreach ($f in $files) {
    $src = Join-Path $MainRepo "CatClawVideo.Core\Services\$f"
    if (Test-Path $src) {
        Copy-Item $src $dest -Force
        Write-Host "synced: $f"
    }
    else {
        Write-Warning "missing: $src （-MainRepo 参数指对了吗）"
        $fail = $true
    }
}

if ($fail) { exit 1 }
Write-Host "done. 记得 git add / commit / push。"
