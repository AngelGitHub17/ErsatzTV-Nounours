<#
    Démarre ErsatzTV Nounours en arrière-plan (sans fenêtre) s'il ne tourne pas déjà,
    puis ouvre l'interface dans le navigateur. -Silencieux : démarre sans ouvrir le navigateur.
#>
param([switch]$Silencieux)

$dossier = Split-Path -Parent $MyInvocation.MyCommand.Path
$url = 'http://localhost:8409/'

if (-not (Get-Process -Name 'ErsatzTV' -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath (Join-Path $dossier 'ErsatzTV.exe') -WorkingDirectory $dossier -WindowStyle Hidden
}

if (-not $Silencieux) {
    # attendre que l'interface réponde (jusqu'à 2 minutes au premier démarrage)
    for ($i = 0; $i -lt 120; $i++) {
        try {
            $reponse = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
            if ($reponse.StatusCode -eq 200) { break }
        }
        catch { }
        Start-Sleep -Seconds 1
    }

    Start-Process $url
}
