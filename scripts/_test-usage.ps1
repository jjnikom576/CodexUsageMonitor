$authPath = Join-Path $env:USERPROFILE '.codex\auth.json'
$auth = Get-Content -Raw $authPath | ConvertFrom-Json
$headers = @{
    Authorization = "Bearer $($auth.tokens.access_token)"
    'chatgpt-account-id' = $auth.tokens.account_id
    Accept = 'application/json'
}
try {
    $r = Invoke-RestMethod -Uri 'https://chatgpt.com/backend-api/wham/usage' -Headers $headers -TimeoutSec 20
    $r | ConvertTo-Json -Depth 6
} catch {
    Write-Output "ERR: $($_.Exception.Message)"
    if ($_.ErrorDetails) { $_.ErrorDetails.Message }
    exit 1
}
