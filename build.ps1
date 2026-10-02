# Builds/publishes SyntheticAgent with all NuGet/dotnet caches kept inside this directory.
# Env vars are set temporarily and restored on exit; nothing system-wide or persistent is changed.
#   .\build.ps1            -> debug build
#   .\build.ps1 publish    -> single-file self-contained exe in .\publish
param([ValidateSet('build', 'publish')][string]$Mode = 'build')

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$overrides = @{
    NUGET_PACKAGES                    = Join-Path $root '.nuget\packages'
    NUGET_HTTP_CACHE_PATH             = Join-Path $root '.nuget\http-cache'
    NUGET_PLUGINS_CACHE_PATH          = Join-Path $root '.nuget\plugins-cache'
    DOTNET_CLI_HOME                   = Join-Path $root '.dotnet-home'
    DOTNET_CLI_TELEMETRY_OPTOUT       = '1'
    DOTNET_NOLOGO                     = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
}

# Remember previous values so an interactive session is left exactly as it was.
$saved = @{}
foreach ($k in $overrides.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process') }

$proj = Join-Path $root 'src\SyntheticAgent\SyntheticAgent.csproj'
try {
    foreach ($k in $overrides.Keys) { [Environment]::SetEnvironmentVariable($k, $overrides[$k], 'Process') }
    if ($Mode -eq 'publish') {
        dotnet publish $proj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o (Join-Path $root 'publish')
    } else {
        dotnet build $proj -c Debug
    }
    $code = $LASTEXITCODE
} finally {
    foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
}
exit $code
