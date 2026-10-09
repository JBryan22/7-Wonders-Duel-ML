# One-off probe for "Capture a mid-game TTS save and verify unknowns on Windows".
# Run while a 7 Wonders Duel game is open in Tabletop Simulator:
#   powershell -ExecutionPolicy Bypass -File docs\research\tts-mod\probe.ps1
# It asks TTS (via the External Editor API) to run a read-only Lua script that
# reports the seats and pawn position, and fires WebRequests at a tiny local
# HTTP server started here, so we learn whether Lua can reach localhost.
# Results are written to probe-results.json next to this script.
# Close any TTS editor plugin (VS Code / Atom) first: they also use port 39998.

$ErrorActionPreference = 'Stop'
$httpPort = 8765
$seconds = 15

$lua = @'
local out = { players = {}, hands = {} }
for _, p in ipairs(Player.getPlayers()) do
  table.insert(out.players, { color = p.color, host = p.host })
end
for _, c in ipairs({ "Red", "White" }) do
  local ok, t = pcall(function() return Player[c].getHandTransform() end)
  if ok and t then out.hands[c] = { x = t.position.x, z = t.position.z } end
end
local pawn = getObjectFromGUID("7ac61a")
if pawn then local p = pawn.getPosition(); out.pawn = { x = p.x, y = p.y, z = p.z } end
local track = getObjectFromGUID("d7240b")
if track then
  local b = track.getBounds()
  out.track = { cx = b.center.x, cz = b.center.z, sx = b.size.x, sz = b.size.z }
end
local function report(label)
  return function(r)
    sendExternalMessage({ probe = label, is_error = r.is_error, error = r.error,
                          code = r.response_code, text = r.text })
  end
end
WebRequest.get("http://localhost:8765/get-localhost", report("get localhost"))
WebRequest.get("http://127.0.0.1:8765/get-ip", report("get 127.0.0.1"))
WebRequest.post("http://127.0.0.1:8765/post-ip", "hello", report("post 127.0.0.1"))
-- Nested tables in sendExternalMessage are silently dropped, and the Execute
-- Lua return value never comes back, so the state travels as a JSON string.
sendExternalMessage({ probe = "state", state = JSON.encode(out) })
'@

$http = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $httpPort)
$editor = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 39998)
$http.Start()
try { $editor.Start() } catch {
    $http.Stop()
    throw "Port 39998 is busy. Close any TTS editor plugin (VS Code/Atom) and retry."
}

$httpHits = @()
$ttsMessages = @()

try {
    $tts = [System.Net.Sockets.TcpClient]::new()
    try { $tts.Connect('127.0.0.1', 39999) } catch {
        throw "Could not reach TTS on port 39999. Is a game loaded in Tabletop Simulator?"
    }
    $msg = @{ messageID = 3; guid = '-1'; script = $lua } | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $tts.GetStream().Write($bytes, 0, $bytes.Length)
    $tts.Close()
    Write-Host "Sent probe to TTS. Listening for $seconds seconds..."

    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ($http.Pending()) {
            $c = $http.AcceptTcpClient()
            $s = $c.GetStream()
            $s.ReadTimeout = 2000
            $buf = New-Object byte[] 8192
            $n = 0
            try { $n = $s.Read($buf, 0, $buf.Length) } catch {}
            $req = [System.Text.Encoding]::ASCII.GetString($buf, 0, $n)
            $line = ($req -split "`r`n")[0]
            $httpHits += $line
            Write-Host "HTTP request received: $line"
            $resp = [System.Text.Encoding]::ASCII.GetBytes(
                "HTTP/1.1 200 OK`r`nContent-Type: text/plain`r`nContent-Length: 2`r`nConnection: close`r`n`r`nok")
            $s.Write($resp, 0, $resp.Length)
            $c.Close()
        }
        if ($editor.Pending()) {
            $c = $editor.AcceptTcpClient()
            # Read until TTS closes the connection or goes quiet for a second.
            $s = $c.GetStream()
            $s.ReadTimeout = 1000
            $ms = [System.IO.MemoryStream]::new()
            $buf = New-Object byte[] 65536
            while ($true) {
                $n = 0
                try { $n = $s.Read($buf, 0, $buf.Length) } catch { break }
                if ($n -le 0) { break }
                $ms.Write($buf, 0, $n)
            }
            $c.Close()
            $text = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
            if ($text) {
                $ttsMessages += $text
                Write-Host "TTS message: $text"
            }
        }
        Start-Sleep -Milliseconds 50
    }
} finally {
    $http.Stop()
    $editor.Stop()
}

$results = [ordered]@{
    when        = (Get-Date).ToString('s')
    httpHits    = $httpHits
    ttsMessages = $ttsMessages
}
$outPath = Join-Path $PSScriptRoot 'probe-results.json'
$results | ConvertTo-Json -Depth 5 | Out-File -Encoding utf8 $outPath
Write-Host ""
Write-Host "Done. $($httpHits.Count) HTTP request(s) reached the local server."
Write-Host "Results written to $outPath"
