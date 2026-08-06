Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$BASE = 'http://localhost:5123/api'

function Conferir($email, $senha) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$BASE/auth/login")
    $req.Content = New-Object System.Net.Http.StringContent((@{email=$email;senha=$senha}|ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
    $login = ($http.SendAsync($req).Result.Content.ReadAsStringAsync().Result | ConvertFrom-Json)
    $tok = $login.token

    function Get_($rota) {
        $r = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$BASE$rota")
        $r.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $tok)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $resp = $http.SendAsync($r).Result
        $corpo = $resp.Content.ReadAsStringAsync().Result
        $sw.Stop()
        [pscustomobject]@{ Status=[int]$resp.StatusCode; Ms=[math]::Round($sw.Elapsed.TotalMilliseconds); Corpo=$corpo }
    }

    Write-Host "`n=========================================================="
    Write-Host " $($login.usuario.empresaNome)   ($email)"
    Write-Host "=========================================================="

    $d = (Get_ '/dashboard')
    $j = $d.Corpo | ConvertFrom-Json
    Write-Host ("  dashboard  {0,4}ms   leadsHoje={1}  aguardando={2}  vendasMes={3}  faturamento={4}  conversao={5}%" -f `
        $d.Ms, $j.leadsHoje, $j.aguardandoResposta, $j.vendasDoMes, [math]::Round($j.faturamentoDoMes), [math]::Round($j.taxaConversao*100,1))
    Write-Host "  funil:   $(($j.funil | ForEach-Object { "$($_.nome)=$($_.contatos)" }) -join '  ')"
    Write-Host "  origens: $(($j.origens | ForEach-Object { "$($_.origem)=$($_.leads)" }) -join '  ')"

    $ini=(Get-Date).AddDays(-119).ToString('yyyy-MM-dd'); $fim=(Get-Date).ToString('yyyy-MM-dd')
    $s = Get_ "/dashboard/serie?de=$ini&ate=$fim"
    $p = ($s.Corpo | ConvertFrom-Json).pontos
    Write-Host ("  serie      {0,4}ms   pontos={1}  dias sem lead={2}  leads={3}  vendas={4}" -f `
        $s.Ms, $p.Count, ($p|Where-Object{$_.leads -eq 0}).Count, ($p|Measure-Object leads -Sum).Sum, ($p|Measure-Object vendas -Sum).Sum)

    foreach ($rota in @('/funil','/contatos?tamanho=30','/conversas?tamanho=30','/meu-dia','/painel/status','/etapas','/dashboard/atividades')) {
        $r = Get_ $rota
        Write-Host ("  {0,-26} {1}  {2,4}ms  {3,7} bytes" -f $rota, $r.Status, $r.Ms, $r.Corpo.Length)
    }

    $md = (Get_ '/meu-dia').Corpo | ConvertFrom-Json
    Write-Host "  Meu Dia: acoes=$($md.acoes.Count)  respondendo=$($md.respondendo)  lembretes=$($md.lembretes)"
}

Conferir 'ana@padaria.com'         'padaria-dev-2026'
Conferir 'ana.demo@nexora.exemplo' 'demonstracao-2026'
