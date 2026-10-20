<#
    Installe (ou met à jour) ErsatzTV — édition Nounours sur Windows :
      - compile le projet dans %LOCALAPPDATA%\Programs\ErsatzTV-Nounours
      - crée les raccourcis Bureau + menu Démarrer (« ErsatzTV Nounours », « Arrêter ErsatzTV Nounours »)
      - démarre ErsatzTV automatiquement à l'ouverture de session (en arrière-plan, sans fenêtre)
    Vos réglages, chaînes et médias (dossier %LOCALAPPDATA%\ersatztv) ne sont jamais touchés.

    Options :
      -SansDemarrageAuto   ne pas lancer ErsatzTV à l'ouverture de session
      -Desinstaller        retirer l'application et ses raccourcis (les données sont conservées)
#>
param(
    [switch]$SansDemarrageAuto,
    [switch]$Desinstaller
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\ErsatzTV-Nounours'
$nom = 'ErsatzTV Nounours'
$bureau = [Environment]::GetFolderPath('Desktop')
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) $nom
$demarrage = [Environment]::GetFolderPath('Startup')
$raccourciAuto = Join-Path $demarrage "$nom (démarrage).lnk"

function Etape($texte) { Write-Host "`n==> $texte" -ForegroundColor Cyan }

function Arreter-ErsatzTV {
    $process = Get-Process -Name 'ErsatzTV', 'ErsatzTV.Scanner' -ErrorAction SilentlyContinue
    if ($process) {
        Write-Host "ErsatzTV est en cours d'exécution : arrêt..." -ForegroundColor Yellow
        $process | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}

function Nouveau-Raccourci($chemin, $script, $arguments, $description) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($chemin)
    $lnk.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $lnk.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$dest\$script`" $arguments".Trim()
    $lnk.WorkingDirectory = $dest
    $lnk.IconLocation = "$dest\ErsatzTV.ico,0"
    $lnk.Description = $description
    $lnk.WindowStyle = 7  # réduit : pas de fenêtre visible
    $lnk.Save()
}

# ------------------------------------------------------------------ désinstallation
if ($Desinstaller) {
    Etape 'Désinstallation'
    Arreter-ErsatzTV
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $bureau "$nom.lnk"), $raccourciAuto
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $menu, $dest
    Write-Host "ErsatzTV Nounours est désinstallé. Vos données restent dans $env:LOCALAPPDATA\ersatztv." -ForegroundColor Green
    return
}

# ------------------------------------------------------------------ vérifications
Etape 'Vérification du SDK .NET 10'
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
    Write-Host "Le SDK .NET 10 est introuvable. Installez-le depuis https://dotnet.microsoft.com/download/dotnet/10.0 puis relancez ce script." -ForegroundColor Red
    exit 1
}

Arreter-ErsatzTV

# ------------------------------------------------------------------ compilation
$sortie = Join-Path $repo 'publish-windows'
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $sortie

$options = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $sortie,
    '-p:PublishSingleFile=true', '-p:DebugType=Embedded', '-p:EnableCompressionInSingleFile=false')

Etape 'Compilation du scanner (quelques minutes la première fois)'
& dotnet publish (Join-Path $repo 'ErsatzTV.Scanner\ErsatzTV.Scanner.csproj') @options
if ($LASTEXITCODE -ne 0) { Write-Host 'Échec de la compilation du scanner.' -ForegroundColor Red; exit 1 }

Etape "Compilation d'ErsatzTV"
& dotnet publish (Join-Path $repo 'ErsatzTV\ErsatzTV.csproj') @options
if ($LASTEXITCODE -ne 0) { Write-Host "Échec de la compilation d'ErsatzTV." -ForegroundColor Red; exit 1 }

# ------------------------------------------------------------------ copie
Etape "Installation dans $dest"
New-Item -ItemType Directory -Force $dest | Out-Null
& robocopy $sortie $dest /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host 'Échec de la copie des fichiers.' -ForegroundColor Red; exit 1 }
Copy-Item -Force (Join-Path $PSScriptRoot 'Lancer.ps1'), (Join-Path $PSScriptRoot 'Arreter.ps1'), (Join-Path $PSScriptRoot 'ErsatzTV.ico') $dest
Get-ChildItem $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $sortie

# ------------------------------------------------------------------ raccourcis
Etape 'Création des raccourcis'
New-Item -ItemType Directory -Force $menu | Out-Null
Nouveau-Raccourci (Join-Path $bureau "$nom.lnk") 'Lancer.ps1' '' 'Démarrer ErsatzTV et ouvrir l''interface'
Nouveau-Raccourci (Join-Path $menu "$nom.lnk") 'Lancer.ps1' '' 'Démarrer ErsatzTV et ouvrir l''interface'
Nouveau-Raccourci (Join-Path $menu "Arrêter $nom.lnk") 'Arreter.ps1' '' 'Arrêter ErsatzTV'

if ($SansDemarrageAuto) {
    Remove-Item -Force -ErrorAction SilentlyContinue $raccourciAuto
    Write-Host 'Démarrage automatique : désactivé.'
}
else {
    Nouveau-Raccourci $raccourciAuto 'Lancer.ps1' '-Silencieux' 'Démarrer ErsatzTV en arrière-plan à l''ouverture de session'
    Write-Host "Démarrage automatique : activé (raccourci dans $demarrage)."
}

# ------------------------------------------------------------------ lancement
Etape 'Démarrage'
& (Join-Path $dest 'Lancer.ps1')
Write-Host "`nC'est prêt ! Interface : http://localhost:8409" -ForegroundColor Green
Write-Host "Raccourcis : « $nom » sur le Bureau et dans le menu Démarrer."
