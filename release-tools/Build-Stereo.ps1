param([string]$Output = 'build/package', [string]$LeiaSdk = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath((Join-Path $repo $Output))
function Publish-Engine($source, $target) {
    & dotnet publish (Join-Path $repo "$source/Ryujinx/Ryujinx.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $target
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $source" }
}
Publish-Engine 'src' $destination
Publish-Engine 'zelda-src' (Join-Path $destination 'engines/zelda')
Copy-Item -LiteralPath (Join-Path $repo 'profiles') -Destination $destination -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $destination 'portable') -Force | Out-Null
$config = Join-Path $destination 'portable/Config.json'
if (!(Test-Path -LiteralPath $config)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Config.clean.json') -Destination $config }
if ($LeiaSdk) {
    $nativeBuild = Join-Path $repo 'build/leia'
    & cmake -S (Join-Path $repo 'native/LeiaBridge') -B $nativeBuild -A x64 "-DLEIASR_SDKROOT=$LeiaSdk"
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge configuration failed' }
    & cmake --build $nativeBuild --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed' }
    foreach ($target in @($destination, (Join-Path $destination 'engines/zelda'))) {
        Copy-Item -LiteralPath (Join-Path $nativeBuild 'Release/ZeldaLeiaBridge.dll') -Destination $target -Force
    }
}
Write-Output "Built Ryujinx Stereo in $destination"
