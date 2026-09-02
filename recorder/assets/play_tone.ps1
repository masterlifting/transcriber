param([string]$Path)
$player = New-Object System.Media.SoundPlayer($Path)
$player.PlaySync()
