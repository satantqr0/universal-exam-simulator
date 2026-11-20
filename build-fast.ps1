param(
  [string]$Destination = (Join-Path $PSScriptRoot 'dist'),
  [string]$SdkDirectory,
  [string]$InstallerDirectory
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$cache = Join-Path $PSScriptRoot '.deps'
if (!$SdkDirectory) {
  $SdkDirectory = Join-Path $cache 'webview-sdk'
  if (!(Test-Path -LiteralPath (Join-Path $SdkDirectory 'lib\net462\Microsoft.Web.WebView2.Core.dll'))) {
    [void](New-Item -Path $cache -ItemType Directory -Force)
    $nuget = Join-Path $cache 'webview2-sdk.zip'
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3719.77/microsoft.web.webview2.1.0.3719.77.nupkg' -OutFile $nuget
    if ((Get-FileHash -LiteralPath $nuget).Hash -ne '2F6BE3A10A1A8D6D1FDE986AF4131DAB344F8181FFFE75590824E0F4B037ED73') {
      throw 'Microsoft WebView2 SDK checksum mismatch.'
    }
    Expand-Archive -LiteralPath $nuget -DestinationPath $SdkDirectory -Force
  }
}
$downloads = [ordered]@{
  'MicrosoftEdgeWebView2RuntimeInstallerX64.exe' = 'https://go.microsoft.com/fwlink/?linkid=2124701'
  'NetFramework462-x64-x86.exe' = 'https://go.microsoft.com/fwlink/?linkid=2099468'
}
if (!$InstallerDirectory) {
  $InstallerDirectory = (New-Item -Path (Join-Path $cache 'installers') -ItemType Directory -Force).FullName
  foreach ($entry in $downloads.GetEnumerator()) {
    $package = Join-Path $InstallerDirectory $entry.Key
    if (!(Test-Path -LiteralPath $package)) { Invoke-WebRequest -Uri $entry.Value -OutFile $package }
  }
}
$output = New-Item -Path $Destination -ItemType Directory -Force
$webRuntime = New-Item -Path (Join-Path $output.FullName '运行组件') -ItemType Directory -Force
$sdk = (Get-Item -LiteralPath $SdkDirectory).FullName
$webCore = Join-Path $sdk 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$webForms = Join-Path $sdk 'lib\net462\Microsoft.Web.WebView2.WinForms.dll'
$webLoader = Join-Path $sdk 'runtimes\win-x64\native\WebView2Loader.dll'
$installerSource = (Get-Item -LiteralPath $InstallerDirectory).FullName
$installerTarget = New-Item -Path (Join-Path $webRuntime.FullName '离线安装包') -ItemType Directory -Force
$cacheFolder = New-Item -Path $cache -ItemType Directory -Force
$checksums = Join-Path $cacheFolder.FullName 'checksums.txt'
$packageHashes = foreach ($name in $downloads.Keys) {
  $package = Join-Path $installerSource $name
  $signature = Get-AuthenticodeSignature -LiteralPath $package
  if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=Microsoft Corporation,') {
    throw "Expected a valid Microsoft signature for $name"
  }
  Copy-Item -LiteralPath $package -Destination $installerTarget.FullName
  "$name $((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash)"
}
[IO.File]::WriteAllLines($checksums, [string[]]$packageHashes)
Copy-Item -LiteralPath $webCore,$webForms,$webLoader,(Join-Path $sdk 'LICENSE.txt'),(Join-Path $sdk 'NOTICE.txt') -Destination $webRuntime.FullName
$libraries = @($webCore, $webForms, $webLoader) | ForEach-Object { "/resource:$_,library.$([IO.Path]::GetFileName($_))" }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Push-Location -LiteralPath $PSScriptRoot
try {
  if (Get-Command node -ErrorAction SilentlyContinue) {
    node --check 'fast-ui/app.js'
    if ($LASTEXITCODE -ne 0) { throw 'JavaScript syntax check failed.' }
  }
  & $csc /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /codepage:65001 "/out:$(Join-Path $output.FullName '通用版考试模拟器.exe')" /win32icon:exam.ico /win32manifest:fast.manifest /resource:exam.ico,exam.ico /resource:fast-ui/index.html,index.html /resource:fast-ui/app.css,app.css /resource:fast-ui/app.js,app.js "/resource:$checksums,installer.checksums" @libraries /reference:System.Windows.Forms.dll,System.Drawing.dll,System.Web.Extensions.dll,System.IO.Compression.dll,System.IO.Compression.FileSystem.dll,System.Xml.Linq.dll,Microsoft.VisualBasic.dll "/reference:$webCore" "/reference:$webForms" FastProgram.cs RuntimeSetup.cs FastWindow.cs FastBridge.cs ExamData.cs MemoryData.cs BankImport.cs BankStore.cs
  if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
} finally {
  Pop-Location
}
