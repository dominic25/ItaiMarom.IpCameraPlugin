param()

$ErrorActionPreference = "Stop"
$store = $PSScriptRoot
$src = Join-Path $store "src"
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) {
    $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
}
if (-not (Test-Path $edge)) {
    throw "Microsoft Edge was not found. It is used as the headless renderer."
}

$slides = Get-ChildItem (Join-Path $src "*.html") |
    Where-Object { $_.Name -match '^\d{2}-' } |
    Sort-Object Name

$profile = Join-Path $env:TEMP "opencode\edge-profile"
foreach ($slide in $slides) {
    $out = Join-Path $store ($slide.BaseName + ".png")
    $url = "file:///" + (($slide.FullName -replace '\\', '/'))
    $log = Join-Path $env:TEMP ("edge-" + $slide.BaseName + ".log")
    $arguments = @(
        "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
        "--allow-file-access-from-files", "--force-device-scale-factor=1",
        "--window-size=1920,1080", "--virtual-time-budget=6000",
        "--user-data-dir=`"$profile`"",
        "--screenshot=`"$out`"",
        "`"$url`""
    )

    $process = Start-Process -FilePath $edge -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
        -RedirectStandardError $log
    if ($process.ExitCode -ne 0) {
        throw "Edge failed for $($slide.Name). See $log."
    }
    if (-not (Test-Path $out)) { throw "No screenshot was written for $($slide.Name)." }
    Write-Output ("{0}  {1:N0} KB" -f $slide.BaseName, ((Get-Item $out).Length / 1KB))
}

# Photo-heavy slides exceed the Store's 1 MB upload limit as truecolour PNGs. Palette compression keeps
# the text legible and the file under the limit.
$compression = @{
    "01-hero" = 256
    "03-modal" = 256
    "05-large-view" = 96
}
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) {
    Write-Warning "ffmpeg was not found; photo slides may exceed 1 MB. Install it or compress them by hand."
} else {
    foreach ($name in $compression.Keys) {
        $path = Join-Path $store ($name + ".png")
        $temporary = Join-Path $store ($name + ".palette.png")
        & $ffmpeg -y -loglevel error -i $path -vf "split[a][b];[a]palettegen=max_colors=$($compression[$name]):stats_mode=full[p];[b][p]paletteuse=dither=sierra2_4a" $temporary
        Move-Item $temporary $path -Force
        Write-Output ("{0}  {1:N0} KB after compression" -f $name, ((Get-Item $path).Length / 1KB))
    }
}
