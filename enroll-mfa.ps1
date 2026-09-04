# One-shot MFA enrollment for a fresh control-plane SuperAdmin.
# Runs login -> enroll -> confirm in a single burst so the 5-minute
# enrollment token cannot expire between steps. Your password is read
# locally and never leaves this machine.

param(
  [string]$ApiBase = "http://localhost:5008/api/v1",
  [string]$Email   = "admin@example.com"
)

$ErrorActionPreference = "Stop"

$sec = Read-Host "Password for $Email" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
$Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)

function Totp([string]$base32) {
  $base32 = $base32.ToUpper().TrimEnd('=')
  $alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
  $bits = ""
  foreach ($c in $base32.ToCharArray()) {
    $val = $alphabet.IndexOf($c)
    if ($val -lt 0) { continue }
    $bits += [Convert]::ToString($val, 2).PadLeft(5, '0')
  }
  $bytes = for ($i = 0; $i + 8 -le $bits.Length; $i += 8) {
    [Convert]::ToByte($bits.Substring($i, 8), 2)
  }
  $key = [byte[]]$bytes
  $counter = [long][Math]::Floor(([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) / 30)
  $cb = [BitConverter]::GetBytes($counter)
  [Array]::Reverse($cb)
  $hmac = New-Object System.Security.Cryptography.HMACSHA1
  $hmac.Key = $key
  $hash = $hmac.ComputeHash($cb)
  $offset = $hash[$hash.Length - 1] -band 0x0f
  $binary = (($hash[$offset] -band 0x7f) -shl 24) -bor (($hash[$offset+1] -band 0xff) -shl 16) -bor (($hash[$offset+2] -band 0xff) -shl 8) -bor ($hash[$offset+3] -band 0xff)
  return ($binary % 1000000).ToString("D6")
}

Write-Host "1/3 login..." -ForegroundColor Cyan
$login = Invoke-RestMethod -Uri "$ApiBase/auth/login" -Method Post -ContentType "application/json" -Body (@{ email = $Email; password = $Password } | ConvertTo-Json)

if ($login.status -ne "mfa_enrollment_required") {
  Write-Host "Unexpected login status: $($login.status). Nothing to enroll (maybe already enrolled)." -ForegroundColor Yellow
  return
}
$token = $login.accessToken
$headers = @{ Authorization = "Bearer $token" }

Write-Host "2/3 enroll..." -ForegroundColor Cyan
$enroll = Invoke-RestMethod -Uri "$ApiBase/auth/mfa/enroll" -Method Post -Headers $headers
$secret = $enroll.secret
Write-Host "   secret: $secret  (save this in an authenticator app too)" -ForegroundColor DarkGray

$code = Totp $secret
Write-Host "3/3 confirm with code $code..." -ForegroundColor Cyan
$confirm = Invoke-RestMethod -Uri "$ApiBase/auth/mfa/confirm" -Method Post -Headers $headers -ContentType "application/json" -Body (@{ code = $code } | ConvertTo-Json)

Write-Host "MFA enrolled. You can now sign in at the dashboard with your password + a code from this secret." -ForegroundColor Green
Write-Host "Add this secret to Google/Microsoft Authenticator for future logins:" -ForegroundColor Green
Write-Host "   $secret" -ForegroundColor White
