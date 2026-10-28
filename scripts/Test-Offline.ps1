param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not (Test-Path -LiteralPath (Join-Path $projectRoot 'CpApis.slnx'))){throw 'Expected the Client Portal API repository'}
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$manifestPath=[IO.Path]::GetFullPath((Join-Path $artifactRoot 'database-object-manifest.json'))
if($manifestPath -cne (Join-Path $artifactRoot 'database-object-manifest.json') -or -not $artifactRoot.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected offline fixture path'}
$owned=$false;$ownedHash=$null;$result=1
try{
 if(-not (Test-Path -LiteralPath $manifestPath)){
  [void][IO.Directory]::CreateDirectory($artifactRoot)
  $bytes=[Text.UTF8Encoding]::new($false).GetBytes('{"generatedAtUtc":"2000-01-01T00:00:00Z","sourceRoot":"offline-test-fixture","databases":[],"objects":[]}')
  $stream=[IO.File]::Open($manifestPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
  $owned=$true;$ownedHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
  Write-Host 'Using a temporary empty manifest for offline test builds only.'
 }
 & dotnet test (Join-Path $projectRoot 'tests/CpApis.Shared.Tests/CpApis.Shared.Tests.csproj') --configuration $Configuration --nologo --verbosity minimal
 $result=$LASTEXITCODE
}finally{
 if($owned -and (Test-Path -LiteralPath $manifestPath)){
  if((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne $ownedHash){throw 'Offline fixture changed; preserved for review'}
  Remove-Item -LiteralPath $manifestPath -Force
 }
}
exit $result
