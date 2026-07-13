$authPath = Join-Path $env:APPDATA 'Cursor\auth.json'
if (-not (Test-Path $authPath)) {
    Write-Output "ERR: Cursor auth.json not found at $authPath"
    exit 1
}

$auth = Get-Content -Raw $authPath | ConvertFrom-Json
$token = $auth.accessToken
if (-not $token) {
    Write-Output 'ERR: missing accessToken'
    exit 1
}

$headers = @{
    Authorization = "Bearer $token"
    Accept = 'application/json'
}

try {
    $r = Invoke-RestMethod -Uri 'https://api2.cursor.sh/auth/usage-summary' -Headers $headers -TimeoutSec 20
    $r | ConvertTo-Json -Depth 8
} catch {
    Write-Output "ERR: $($_.Exception.Message)"
    if ($_.ErrorDetails) { $_.ErrorDetails.Message }
    exit 1
}
