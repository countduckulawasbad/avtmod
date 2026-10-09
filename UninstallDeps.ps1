# Avatar - Personas: Uninstall Runtime Dependencies (Keep ComfyUI + Models)
# Removes: Python packages (pillow, requests, transparent_background) + InSPyReNet node
# Preserves: ComfyUI portable, SDXL checkpoint, RimWorld LoRA, cached state

$rimworldDataPath = "$env:APPDATA\..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios"
$modSettingsPath = "$rimworldDataPath\Config\ModsConfig.xml"

# Default ComfyUI install location (check mod settings first)
$comfyDefault = "$env:APPDATA\..\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\avatar"

Write-Host "=== Avatar - Personas Dependency Uninstaller ===" -ForegroundColor Cyan
Write-Host ""

# Try to find ComfyUI path from mod settings
$comfyPath = $null
if (Test-Path $modSettingsPath) {
    # This is a heuristic; the actual path comes from ModSettings JSON
    # For now we'll use the default
}

$comfyPath = $comfyDefault
if (-not (Test-Path $comfyPath)) {
    Write-Host "Warning: ComfyUI not found at $comfyPath" -ForegroundColor Yellow
    Write-Host "If installed elsewhere, update \$comfyPath in this script." -ForegroundColor Yellow
}

Write-Host "ComfyUI path: $comfyPath" -ForegroundColor Cyan
Write-Host ""

# 1. Uninstall Python packages
Write-Host "Step 1: Uninstalling Python packages..." -ForegroundColor Yellow
$pythonExe = "$comfyPath\python_embedded\python.exe"

if (Test-Path $pythonExe) {
    $packages = @("pillow", "requests", "transparent_background")
    foreach ($pkg in $packages) {
        Write-Host "  Uninstalling $pkg..." -ForegroundColor Gray
        & $pythonExe -m pip uninstall -y $pkg 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "    [OK] $pkg uninstalled" -ForegroundColor Green
        } else {
            Write-Host "    [--] $pkg not found or already removed" -ForegroundColor Gray
        }
    }
} else {
    Write-Host "  [X] python_embedded not found at $pythonExe" -ForegroundColor Red
    Write-Host "    Skipping pip uninstalls" -ForegroundColor Gray
}

Write-Host ""

# 2. Remove InSPyReNet node
Write-Host "Step 2: Removing InSPyReNet node..." -ForegroundColor Yellow
$inspyrenetPath = "$comfyPath\custom_nodes\ComfyUI-Inspyrenet-Rembg"

if (Test-Path $inspyrenetPath) {
    Remove-Item -Path $inspyrenetPath -Recurse -Force -ErrorAction SilentlyContinue
    if ($?) {
        Write-Host "  [OK] InSPyReNet node removed" -ForegroundColor Green
    } else {
        Write-Host "  [X] Failed to remove InSPyReNet node (may be in use)" -ForegroundColor Red
    }
} else {
    Write-Host "  [--] InSPyReNet node not found at $inspyrenetPath" -ForegroundColor Gray
}

Write-Host ""

# 3. Summary
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "[PRESERVED]:" -ForegroundColor Green
Write-Host "  - ComfyUI portable installation"
Write-Host "  - SDXL checkpoint"
Write-Host "  - RimWorld LoRA"
Write-Host "  - Cached mod state (will retrigger setup on game load)"
Write-Host ""
Write-Host "[REMOVED]:" -ForegroundColor Yellow
Write-Host "  - Python dependencies (pillow, requests, transparent_background)"
Write-Host "  - InSPyReNet node"
Write-Host ""
Write-Host "Next: Start RimWorld with Avatar - Personas enabled." -ForegroundColor Cyan
Write-Host "The mod will auto-detect the existing ComfyUI and reinstall just the missing pieces." -ForegroundColor Cyan
