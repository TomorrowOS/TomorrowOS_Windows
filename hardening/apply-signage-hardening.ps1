# Optional Windows 11 Pro signage hardening for TomorrowOS.
# Does NOT disable Windows security updates.

$ErrorActionPreference = "Continue"

Write-Host "Applying TomorrowOS signage hardening..."

# Hibernate is applied by Setup when "Disable hibernation" is on — do not
# write it here, or that toggle being off would still disable hibernation.

# Screensaver is applied by Setup when "Disable screen saver" is on — do not
# write it here, or that toggle being off would still kill the screen saver.

# Reduce consumer / tip noise (current user)
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338389Enabled /t REG_DWORD /d 0 /f | Out-Null
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SoftLandingEnabled /t REG_DWORD /d 0 /f | Out-Null

# Game Bar / capture overlays are applied by Setup when "Disable fullscreen game
# overlays" is on — do not write them here.

# Windows Update maintenance window / Active Hours are applied by Setup when
# "Configure Windows update maintenance window" is on — do not write them here.

# Focus assist: priority only (best-effort)
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings" /v NOC_GLOBAL_SETTING_TOASTS_ENABLED /t REG_DWORD /d 0 /f | Out-Null

Write-Host "Hardening script finished. Review group policy / update rings separately for fleet deployments."
