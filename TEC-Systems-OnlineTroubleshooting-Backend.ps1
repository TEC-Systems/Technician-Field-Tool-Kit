param(
    [int]$Port = 8787,
    [string]$Model = ''
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Write-BackendLog {
    param([string]$Message)
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host ('[{0}] {1}' -f $stamp, $Message)
}

function Get-JsonBody {
    param($Request)

    $reader = New-Object System.IO.StreamReader($Request.InputStream, $Request.ContentEncoding)
    try {
        $raw = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return ($raw | ConvertFrom-Json)
}

function Send-JsonResponse {
    param(
        $Context,
        [int]$StatusCode,
        $Body
    )

    $json = $Body | ConvertTo-Json -Depth 10
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode = $StatusCode
    $Context.Response.ContentType = 'application/json; charset=utf-8'
    $Context.Response.ContentEncoding = [System.Text.Encoding]::UTF8
    $Context.Response.Headers['Access-Control-Allow-Origin'] = '*'
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.OutputStream.Close()
}

function Get-ResponseOutputText {
    param($Response)

    if ($Response.PSObject.Properties['output_text'] -and $Response.output_text) {
        return [string]$Response.output_text
    }

    $parts = @()
    foreach ($item in @($Response.output)) {
        foreach ($content in @($item.content)) {
            if ($content.PSObject.Properties['text'] -and $content.text) {
                $parts += [string]$content.text
            }
        }
    }

    return ($parts -join [Environment]::NewLine)
}

function Get-ResponseSources {
    param($Response)

    $sources = @()

    foreach ($item in @($Response.output)) {
        if ($item.type -eq 'web_search_call' -and $item.PSObject.Properties['action']) {
            foreach ($source in @($item.action.sources)) {
                if ($source.url) {
                    $sources += [pscustomobject]@{
                        title = if ($source.PSObject.Properties['title'] -and $source.title) { [string]$source.title } else { [string]$source.url }
                        url = [string]$source.url
                    }
                }
            }
        }

        foreach ($content in @($item.content)) {
            foreach ($annotation in @($content.annotations)) {
                if ($annotation.type -eq 'url_citation' -and $annotation.url) {
                    $sources += [pscustomobject]@{
                        title = if ($annotation.PSObject.Properties['title'] -and $annotation.title) { [string]$annotation.title } else { [string]$annotation.url }
                        url = [string]$annotation.url
                    }
                }
            }
        }
    }

    $unique = @()
    $seen = @{}
    foreach ($source in $sources) {
        if (-not $seen.ContainsKey($source.url)) {
            $seen[$source.url] = $true
            $unique += $source
        }
    }

    return $unique
}

function Invoke-OpenAiOnlineTroubleshooting {
    param($Payload)

    $apiKey = $env:OPENAI_API_KEY
    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        throw 'OPENAI_API_KEY is not set for the backend.'
    }

    $resolvedModel = if ([string]::IsNullOrWhiteSpace($Model)) {
        if ([string]::IsNullOrWhiteSpace($env:OPENAI_MODEL)) { 'gpt-5' } else { $env:OPENAI_MODEL }
    }
    else {
        $Model
    }

    $issue = if ($Payload.PSObject.Properties['issue']) { [string]$Payload.issue } else { '' }
    $hostname = if ($Payload.PSObject.Properties['hostname']) { [string]$Payload.hostname } else { '' }
    $internetStatus = if ($Payload.PSObject.Properties['internet_status']) { [string]$Payload.internet_status } else { '' }
    $bmsContext = if ($Payload.PSObject.Properties['bms_context']) { [string]$Payload.bms_context } else { '' }
    $recentLog = if ($Payload.PSObject.Properties['recent_log']) { [string]$Payload.recent_log } else { '' }
    $adapter = if ($Payload.PSObject.Properties['adapter']) { [string]$Payload.adapter } else { '' }

    $instructions = @"
You are the TEC Systems Field Toolkit online troubleshooting assistant.

Respond for an IT field technician. Keep the answer practical, safe, and concise.
Always return these sections in plain text:
1. Short explanation
2. Step-by-step fix
3. Commands to try
4. Source links

Requirements:
- Prefer Microsoft, vendor, or standards-based sources when available.
- Do not invent commands or registry paths.
- Mark destructive or high-risk actions clearly.
- If the issue is ambiguous, say what to verify next.
"@

    $input = @"
Technician issue:
$issue

Toolkit context:
- Hostname: $hostname
- Internet status: $internetStatus
- Selected adapter: $adapter

BMS context:
$bmsContext

Recent technician log:
$recentLog
"@

    $body = @{
        model = $resolvedModel
        reasoning = @{
            effort = 'low'
        }
        tools = @(
            @{
                type = 'web_search'
                external_web_access = $true
                user_location = @{
                    type = 'approximate'
                    country = 'US'
                    timezone = 'America/New_York'
                }
            }
        )
        include = @('web_search_call.action.sources')
        tool_choice = 'auto'
        instructions = $instructions
        input = $input
    }

    $headers = @{
        Authorization = ('Bearer {0}' -f $apiKey)
        'Content-Type' = 'application/json'
    }

    $response = Invoke-RestMethod -Method Post -Uri 'https://api.openai.com/v1/responses' -Headers $headers -Body ($body | ConvertTo-Json -Depth 12) -TimeoutSec 120 -ErrorAction Stop

    return [pscustomobject]@{
        ok = $true
        model = $resolvedModel
        answer = Get-ResponseOutputText -Response $response
        sources = Get-ResponseSources -Response $response
        received_at = (Get-Date).ToString('o')
    }
}

$prefix = 'http://127.0.0.1:{0}/' -f $Port
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($prefix)
$listener.Start()

Write-BackendLog ('TEC Systems Online Troubleshooting Backend listening at {0}' -f $prefix)
Write-BackendLog 'POST /troubleshoot expects JSON with issue/context and uses OPENAI_API_KEY from the backend environment.'

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $path = $request.Url.AbsolutePath.Trim('/').ToLowerInvariant()

        try {
            if ($request.HttpMethod -eq 'GET' -and $path -eq 'health') {
                Send-JsonResponse -Context $context -StatusCode 200 -Body @{
                    ok = $true
                    service = 'TEC Systems Online Troubleshooting Backend'
                    model = if ([string]::IsNullOrWhiteSpace($Model)) { if ([string]::IsNullOrWhiteSpace($env:OPENAI_MODEL)) { 'gpt-5' } else { $env:OPENAI_MODEL } } else { $Model }
                    api_key_present = (-not [string]::IsNullOrWhiteSpace($env:OPENAI_API_KEY))
                    time = (Get-Date).ToString('o')
                }
                continue
            }

            if ($request.HttpMethod -eq 'POST' -and $path -eq 'troubleshoot') {
                $payload = Get-JsonBody -Request $request
                if (-not $payload) {
                    Send-JsonResponse -Context $context -StatusCode 400 -Body @{ ok = $false; error = 'Request body was empty.' }
                    continue
                }

                $issueText = if ($payload.PSObject.Properties['issue']) { [string]$payload.issue } else { '' }
                if ([string]::IsNullOrWhiteSpace($issueText)) {
                    Send-JsonResponse -Context $context -StatusCode 400 -Body @{ ok = $false; error = 'The request did not include an issue field.' }
                    continue
                }

                Write-BackendLog ('Troubleshooting request received: {0}' -f $issueText)
                $result = Invoke-OpenAiOnlineTroubleshooting -Payload $payload
                Send-JsonResponse -Context $context -StatusCode 200 -Body $result
                continue
            }

            Send-JsonResponse -Context $context -StatusCode 404 -Body @{ ok = $false; error = 'Route not found.' }
        }
        catch {
            Write-BackendLog ('Request failed: {0}' -f $_.Exception.Message)
            Send-JsonResponse -Context $context -StatusCode 500 -Body @{ ok = $false; error = $_.Exception.Message }
        }
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
