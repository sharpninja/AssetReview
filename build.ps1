param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $BuildArguments
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '_build\_build.csproj'
dotnet run --project $project -- @BuildArguments
exit $LASTEXITCODE
