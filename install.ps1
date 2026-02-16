# Windows installer - downloads latest release
Write-Host "Soulman Windows installer"
Write-Host "TODO: implement release download"
# For now: clone and build
$installDir = "$env:LOCALAPPDATA\Soulman"
if (Test-Path "$installDir\.git") { git -C $installDir pull } else { git clone https://github.com/SlimeQ/soulman.git $installDir }
dotnet build "$installDir\src\Soulman.Windows\"
Write-Host "Run with: dotnet run --project $installDir\src\Soulman.Windows\"
