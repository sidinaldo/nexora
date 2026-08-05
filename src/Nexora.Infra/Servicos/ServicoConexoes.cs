using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A conexao de WhatsApp do tenant logado.
///
/// O isolamento aqui e o HasQueryFilter global: toda consulta a Conexoes ja sai filtrada pela
/// empresa do JWT. Diferente do processador do webhook, este servico roda DENTRO de requisicao
/// autenticada — nao ha IgnoreQueryFilters nenhum, e nao deve haver.</summary>
public class ServicoConexoes(
    NexoraDbContext db,
    IClienteWhatsApp cliente,
    TimeProvider relogio) : IServicoConexoes
{
    public async Task<ConexaoDto?> MinhaAsync(CancellationToken ct) =>
        await db.Conexoes.AsNoTracking()
            .Select(c => new ConexaoDto(
                c.Id, c.Nome, c.InstanceName, c.Numero, c.NumeroAnterior,
                c.PerfilNome, c.PerfilFotoUrl, c.Status.ToString().ToLowerInvariant(),
                c.ConectadoEm, c.DesconectadoEm))
            .FirstOrDefaultAsync(ct);

    public async Task<StatusConexaoDto> StatusAsync(CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(ct);
        var estado = await cliente.StatusInstanciaAsync(conexao.InstanceName, ct);
        var conectado = estado == "open";

        // Persiste o status para o banner ficar fresco mesmo se o webhook se perder. So escreve
        // quando MUDA — a tela chama isto em polling de 3s, e sem o guard seria uma escrita a
        // cada tick.
        //
        // 'offline' e distinto de 'desconectado': offline = a Evolution nao respondeu (problema
        // nosso, o cliente nao tem o que fazer); desconectado = o numero caiu e ele precisa
        // reparear. Colapsar os dois manda a pessoa escanear QR a toa.
        var novo = estado switch
        {
            "open" => StatusConexao.Conectado,
            "connecting" => StatusConexao.Conectando,
            "nao_criada" => StatusConexao.NaoCriada,
            "offline" => StatusConexao.Offline,
            _ => StatusConexao.Desconectado
        };

        var agora = relogio.GetUtcNow().UtcDateTime;
        var mudou = false;

        if (conexao.Status != novo)
        {
            conexao.Status = novo;
            conexao.StatusEm = agora;
            if (conectado && conexao.ConectadoEm is null) conexao.ConectadoEm = agora;
            if (!conectado && novo == StatusConexao.Desconectado) conexao.DesconectadoEm = agora;
            mudou = true;
        }

        // Rede de seguranca: conectada mas sem numero (webhook connection.update perdido) ->
        // backfill do numero e do perfil.
        if (conectado && string.IsNullOrEmpty(conexao.Numero))
        {
            var det = await cliente.ObterDetalhesInstanciaAsync(conexao.InstanceName, ct);
            if (det?.OwnerJid is { Length: > 0 } jid)
            {
                conexao.Numero = CanonicalizadorTelefone.Canonicalizar(jid.Split('@')[0]);
                conexao.PerfilNome ??= det.PerfilNome;
                conexao.PerfilFotoUrl ??= det.PerfilFotoUrl;
                mudou = true;
            }
        }

        if (mudou) await db.SaveChangesAsync(ct);

        return new StatusConexaoDto(conexao.InstanceName, estado, conectado);
    }

    public async Task<QrCodeDto> ConectarAsync(CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(ct);
        var qr = await cliente.ConectarInstanciaAsync(conexao.InstanceName, null, ct);
        return new QrCodeDto(qr.Base64, qr.Codigo, qr.PairingCode, qr.Estado, qr.Estado == "open");
    }

    public async Task<QrCodeDto> ParearAsync(string numero, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(ct);

        var canon = CanonicalizadorTelefone.Canonicalizar(numero ?? "");
        if (!CanonicalizadorTelefone.EhValido(canon))
            throw new RegraDeNegocioException("Informe o número com DDD para gerar o código de pareamento.");

        var qr = await cliente.ConectarInstanciaAsync(conexao.InstanceName, canon, ct);
        return new QrCodeDto(qr.Base64, qr.Codigo, qr.PairingCode, qr.Estado, qr.Estado == "open");
    }

    public async Task DesconectarAsync(CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(ct);
        await cliente.DesconectarInstanciaAsync(conexao.InstanceName, ct);

        // Reflete de imediato. O webhook connection.update confirma depois, mas nao dependemos
        // dele: se ele se perder, a tela ficaria mostrando "conectado" para sempre.
        if (conexao.Status != StatusConexao.Desconectado)
        {
            var agora = relogio.GetUtcNow().UtcDateTime;
            conexao.Status = StatusConexao.Desconectado;
            conexao.StatusEm = agora;
            conexao.DesconectadoEm = agora;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<SaudeConexaoDto> SaudeAsync(CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        var inicioDoDia = new DateTime(agora.Year, agora.Month, agora.Day, 0, 0, 0, DateTimeKind.Utc);

        // O query filter global ja restringe ao tenant — este servico roda autenticado.
        var saidas = db.Mensagens.AsNoTracking().Where(m => m.Direcao == DirecaoMensagem.Saida);

        return new SaudeConexaoDto(
            EnviadasHoje: await saidas.CountAsync(m => m.EnviadaEm >= inicioDoDia, ct),

            // Ainda vai ser tentada: reservada, nao despachada, nao expirada.
            Pendentes: await saidas.CountAsync(
                m => m.EnviadaEm == null && m.ExpiradaEm == null && m.LembreteId != null, ct),

            // Passou da janela de reenvio: NAO sera mais tentada. E o numero que exige acao
            // humana, e por isso nao pode ficar somado ao de cima.
            Expiradas: await saidas.CountAsync(m => m.ExpiradaEm != null, ct),

            FalhasHoje: await saidas.CountAsync(
                m => m.Erro != null && m.EnviadaEm == null && m.ReservadoEm >= inicioDoDia, ct));
    }

    public async Task ReconhecerTrocaAsync(CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(ct);
        if (conexao.NumeroAnterior is null) return;
        conexao.NumeroAnterior = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A conexao do tenant. Uma por empresa na fase 1, criada no cadastro — se nao
    /// existe, algo quebrou no cadastro e a mensagem tem que dizer isso.</summary>
    private async Task<Conexao> MinhaConexaoAsync(CancellationToken ct) =>
        await db.Conexoes.FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException(
                "Esta empresa não tem conexão de WhatsApp configurada. Fale com o suporte.");
}
