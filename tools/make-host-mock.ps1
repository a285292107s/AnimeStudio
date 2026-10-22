# 重新生成 Host UI 的演示数据
#
# Host UI（AnimeStudio.Host/wwwroot）默认用 mock 数据运行，方便在普通浏览器里
# 迭代前端。mock 数据来自本机游戏导出，属于游戏本体资源，**不入库**。
# 换机器或重新 clone 后跑一次这个脚本即可恢复演示效果。
#
# 前置：先用 CLI 导出一批 UI 精灵，再用 --textmap 导出文本表（见 README / 交付说明）。
#
#   .\tools\make-host-mock.ps1 `
#       -SpriteDir "$env:TEMP\as-probe-ui\Sprite" `
#       -TextMap   "$env:TEMP\as-textmap2\textmap.zh-CN.0.0.16566615.json"
#
# 产出：
#   AnimeStudio.Host/wwwroot/mock/data.json      索引 + 统计 + 120 条文案
#   AnimeStudio.Host/wwwroot/mock/images/*.png   最多 28 张缩略图

param(
    [Parameter(Mandatory = $true)][string]$SpriteDir,
    [Parameter(Mandatory = $true)][string]$TextMap,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\AnimeStudio.Host\wwwroot'),
    [int]$ImageCount = 28
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $SpriteDir)) { throw "找不到精灵目录：$SpriteDir" }
if (-not (Test-Path $TextMap)) { throw "找不到文本表：$TextMap" }

$imageOut = Join-Path $OutDir 'mock\images'
New-Item -ItemType Directory -Force $imageOut | Out-Null
Remove-Item (Join-Path $imageOut '*.png') -Force -ErrorAction SilentlyContinue

function Get-PngSize([string]$path) {
    $bytes = New-Object byte[] 34
    $stream = [System.IO.File]::OpenRead($path)
    $null = $stream.Read($bytes, 0, 34)
    $stream.Close()
    [pscustomobject]@{
        W = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
        H = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
    }
}

$candidates = Get-ChildItem $SpriteDir -File -Filter *.png |
    Where-Object { $_.Length -ge 6000 -and $_.Length -le 400000 }

$small = @($candidates | Where-Object { $_.Length -lt 60000 } | Get-Random -Count ([Math]::Min(20, @($candidates | Where-Object { $_.Length -lt 60000 }).Count)))
$medium = @($candidates | Where-Object { $_.Length -ge 60000 } | Get-Random -Count ([Math]::Min(8, @($candidates | Where-Object { $_.Length -ge 60000 }).Count)))
$picked = @($small + $medium) | Select-Object -First $ImageCount

$containers = @(
    'assets/asbres/spriteoutput/ui/quest',
    'assets/asbres/spriteoutput/ui/activity',
    'assets/asbres/spriteoutput/ui/maze',
    'assets/asbres/spriteoutput/ui/itemicon',
    'assets/asbres/spriteoutput/ui/map',
    'assets/asbres/spriteoutput/ui/team'
)

$images = @()
$index = 0
foreach ($file in $picked) {
    $index++
    Copy-Item $file.FullName (Join-Path $imageOut $file.Name) -Force
    $size = Get-PngSize $file.FullName
    $images += [pscustomobject]@{
        id        = "img$index"
        name      = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        file      = "mock/images/$($file.Name)"
        w         = $size.W
        h         = $size.H
        kb        = [math]::Round($file.Length / 1KB, 1)
        container = $containers[($index - 1) % $containers.Count]
        kind      = 'Sprite'
        block     = 'StreamingAssets/Asb/Windows/' + ('{0:x16}' -f (Get-Random -Minimum 1000000 -Maximum 9999999)) + '.block'
    }
}

$texts = @()
foreach ($line in (Get-Content $TextMap -Encoding utf8 -TotalCount 400)) {
    if ($line -match '^\s*"(\d{10,20})":\s*(.+?),?$') {
        $hash = $Matches[1]
        try { $value = $Matches[2].TrimEnd(',') | ConvertFrom-Json } catch { continue }
        if ($value -isnot [string] -or $value.Trim().Length -eq 0) { continue }
        $texts += [pscustomobject]@{ hash = $hash; text = $value; source = 'TextMapCHS' }
        if ($texts.Count -ge 120) { break }
    }
}

# 这些统计是 HSR 实测值；换游戏时改这里。
$payload = [pscustomobject]@{
    game = [pscustomobject]@{
        id = 'SR'; name = '星穹铁道'; short = 'HSR'
        version = '0.0.16566615'
        path = 'C:\Program Files\miHoYo Launcher\games\Star Rail Game\StarRail_Data'
    }
    games = @(
        [pscustomobject]@{ id='SR'; name='星穹铁道'; short='HSR'; status='ready'; version='0.0.16566615' },
        [pscustomobject]@{ id='ZZZ'; name='绝区零'; short='ZZZ'; status='probe'; version='—' }
    )
    stats = [pscustomobject]@{
        assets = 7312592; blocks = 9033; textEntries = 474193; emptyStrings = 6246
        parsedStrings = 480439; textMaps = 1; sprites = 58269; uiSprites = 17669
        textures = 108831; uiTextures = 23494; indexBytes = 1963674547
    }
    languages = @([pscustomobject]@{ code='zh-CN'; folder='cn'; label='简体中文'; entries=474193 })
    containers = @(
        [pscustomobject]@{ id='spriteoutput/ui'; label='spriteoutput/ui'; count=17669 },
        [pscustomobject]@{ id='spriteoutput/quest'; label='spriteoutput/quest'; count=5123 },
        [pscustomobject]@{ id='spriteoutput/itemicon'; label='spriteoutput/itemicon'; count=1908 },
        [pscustomobject]@{ id='ui/ui_texture'; label='ui/ui_texture'; count=2145 },
        [pscustomobject]@{ id='spriteoutput/mappics'; label='spriteoutput/mappics'; count=2254 }
    )
    images = $images
    texts = $texts
    log = @(
        '[Info] design ver : v0.0.16566615',
        '[Info] parsed     : 480,439 strings',
        '[Info] textmap    : 474,193 entries (dropped 6,246 empty)',
        '[Info] index      : 7,312,592 assets / 9,033 blocks',
        '[Info] ready'
    )
}

New-Item -ItemType Directory -Force (Join-Path $OutDir 'mock') | Out-Null
$payload | ConvertTo-Json -Depth 8 | Set-Content -Path (Join-Path $OutDir 'mock\data.json') -Encoding utf8

Write-Host ("mock 数据已生成：{0} 张图 / {1} 条文案" -f $images.Count, $texts.Count)
