$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Windows .NET Framework C# compiler not found.' }
$outputDirectory = Join-Path $projectDirectory 'dist'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$executablePath = Join-Path $outputDirectory 'CodexVpsLauncher.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ "/out:$executablePath" "/win32manifest:$projectDirectory\src\launcher.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'connection.example.ini') -Destination $outputDirectory
Write-Output "Built $executablePath"
