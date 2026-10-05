$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'App/App.csproj'
dotnet run --project $project
if ($LASTEXITCODE -ne 0) {
    throw "Aplikasi berhenti dengan exit code $LASTEXITCODE."
}
