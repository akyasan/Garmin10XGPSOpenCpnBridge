$ErrorActionPreference = "Stop"

$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$source = Join-Path $PSScriptRoot "ProgramTcp.cs"
$outputDirectory = Join-Path $PSScriptRoot "bin"
$output = Join-Path $outputDirectory "GarminGpsBridge.exe"

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "The .NET Framework C# compiler was not found at $compiler"
}

if (-not (Test-Path -LiteralPath $source)) {
    throw "Source file not found: $source"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $compiler /nologo /target:winexe /platform:anycpu /optimize `
    /reference:System.dll /reference:System.Core.dll `
    /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    "/out:$output" $source

if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE"
}

Write-Output $output
