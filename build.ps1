# Compila BluetoothBatteryBar.cs con el compilador de C# incluido en Windows.
# Resultado: dist\BluetoothBatteryBar.exe, un único archivo que sirve de instalador
# (al abrirlo en cualquier PC pregunta si instalarlo).
#   .\build.ps1            solo compila
#   .\build.ps1 -Install   compila e instala/actualiza en este equipo sin preguntar
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$dist = "$PSScriptRoot\dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$cscArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/win32manifest:$PSScriptRoot\app.manifest",
    "/out:$dist\BluetoothBatteryBar.exe",
    "/r:$fw\WPF\PresentationFramework.dll", "/r:$fw\WPF\PresentationCore.dll",
    "/r:$fw\WPF\WindowsBase.dll", "/r:$fw\System.Xaml.dll",
    "$PSScriptRoot\BluetoothBatteryBar.cs"
)
& "$fw\csc.exe" @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Error de compilacion" }
"Compilado: $dist\BluetoothBatteryBar.exe"
if ($Install) {
    # Sin -Wait: esperaría también al widget instalado, que no se cierra nunca.
    Start-Process "$dist\BluetoothBatteryBar.exe" -ArgumentList '--install'
    Start-Sleep -Seconds 3
    "Instalado en $env:LOCALAPPDATA\BluetoothBatteryBar"
}
