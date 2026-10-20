# Arrête ErsatzTV Nounours (et une analyse de médiathèque en cours).
Get-Process -Name 'ErsatzTV', 'ErsatzTV.Scanner' -ErrorAction SilentlyContinue | Stop-Process -Force
