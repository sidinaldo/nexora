using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nexora.Core.Captacao;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Webhooks;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;
using Nexora.Infra.Whatsapp;

namespace Nexora.Infra.Evolution;

/// <summary>Recebe os eventos do webhook da Evolution e os traduz para o dominio do Nexora.
///
/// ================== SEM TENANT NO CONTEXTO ==================
/// Roda FORA de requisicao autenticada (a Evolution nao se autentica como usuario), entao
/// IContextoEmpresa.EmpresaId vale 0 e o query filter global faz TODA consulta voltar VAZIA em
/// silencio. Por isso cada acesso a dado aqui usa .IgnoreQueryFilters() MAIS filtro explicito
/// por empresaId. A unica excecao e a busca da conexao por instance_name — chave global por
/// construcao, e justamente como o tenant e descoberto.
/// ============================================================
///
/// Amputado do Recupera: saiu a abertura de ticket por divida (la a thread era por devedor e o
/// ticket por recebivel, porque cada credor negocia a sua). No Nexora um contato = uma
/// conversa, e o ramo "numero fora da carteira" virou CRIAR CONTATO — a captura de lead da
/// fase 1.</summary>
public class ProcessadorEventoEvolution(
    NexoraDbContext db,
    IClienteWhatsApp whatsapp,
    IArmazenamentoMidia armazenamento,
    INotificadorPainel painel,
    IPublicadorEventos eventos,
    IPublicadorConversoes conversoes,
    ILeituraDaResposta leituraNps,
    TimeProvider relogio,
    ILogger<ProcessadorEventoEvolution> log) : IProcessadorWebhookWhatsApp
{
    /// <summary>O caminho compartilhado de toda mensagem recebida. Criado aqui, e nao injetado,
    /// para o construtor deste processador continuar o mesmo — e os testes que o montam a mao
    /// tambem.</summary>
    private RecepcaoMensagem? recepcao;

    private RecepcaoMensagem Recepcao =>
        recepcao ??= new RecepcaoMensagem(
            db, armazenamento, painel, eventos, conversoes, leituraNps, relogio, log);

    /// <summary>O parse do payload da Evolution mora AQUI, na Infra — nao no controller.
    /// E o unico lugar do sistema que conhece o formato dela.
    ///
    /// Nao lanca: a Evolution reentrega ate receber 2xx, entao um payload que a gente nao sabe
    /// processar ficaria em loop eterno. O corpo vai para o log para investigar.</summary>
    public async Task ProcessarAsync(string payloadJson, CancellationToken ct)
    {
        try
        {
            var evento = JsonSerializer.Deserialize<EventoEvolution>(payloadJson);
            if (evento is not null)
                await ProcessarAsync(evento, payloadJson, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Falha ao processar webhook. Payload: {Payload}", payloadJson);
        }
    }

    private async Task ProcessarAsync(EventoEvolution ev, string payloadCru, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ev.Instance)) return;

        // O TENANT SAI DAQUI. instance_name e unico globalmente (uq_conexoes_instance), entao
        // este e o unico IgnoreQueryFilters do arquivo que dispensa filtro por empresaId — a
        // propria chave ja e global.
        var conexao = await db.Conexoes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.InstanceName == ev.Instance, ct);

        if (conexao is null)
        {
            log.LogWarning("Webhook da instancia desconhecida '{Instance}' — ignorado.", ev.Instance);
            return;
        }

        switch (ev.Evento?.ToLowerInvariant())
        {
            case "messages.upsert":
                await ProcessarMensagemAsync(conexao, ev, payloadCru, ct);
                break;
            case "messages.update":
                await ProcessarAckAsync(conexao, ev, ct);
                break;
            case "connection.update":
                await ProcessarConexaoAsync(conexao, ev, ct);
                break;
            default:
                // qrcode.updated etc.: nao ha o que persistir na fase 1.
                break;
        }
    }

    // ==================================================================== conexao
    /// <summary>connection.update: o WhatsApp conectou ou caiu. Ao CONECTAR (state=open)
    /// descobre o numero real (ownerJid via fetchInstances) e o perfil. Ao CAIR marca
    /// desconectado — o que serve de freio para o envio e acende o banner global.
    /// Nunca lanca (o webhook precisa responder 2xx).</summary>
    private async Task ProcessarConexaoAsync(Conexao conexao, EventoEvolution ev, CancellationToken ct)
    {
        var state = ev.Data?.State?.ToLowerInvariant();
        if (state is null) return;

        var agora = relogio.GetUtcNow().UtcDateTime;

        if (state == "open")
        {
            var detalhes = await whatsapp.ObterDetalhesInstanciaAsync(conexao.InstanceName, ct);

            var numero = detalhes?.OwnerJid is { Length: > 0 } jid
                ? CanonicalizadorTelefone.Canonicalizar(jid.Split('@')[0])
                : conexao.Numero;

            // TROCA DE NUMERO: conectou um chip DIFERENTE do anterior. Nao bloqueia — o webhook
            // e assincrono e nao ha usuario no loop para confirmar. Grava o novo, guarda o
            // antigo em numero_anterior para a tela avisar depois, e loga. O historico de
            // conversa e por CONTATO, entao nada se perde.
            if (!string.IsNullOrEmpty(conexao.Numero) && !string.IsNullOrEmpty(numero)
                && conexao.Numero != numero)
            {
                conexao.NumeroAnterior = conexao.Numero;
                log.LogWarning("Conexao {Id} conectou numero diferente: {Antigo} -> {Novo}.",
                    conexao.Id, conexao.Numero, numero);
            }

            if (!string.IsNullOrEmpty(numero)) conexao.Numero = numero;
            if (detalhes?.PerfilNome is not null) conexao.PerfilNome = detalhes.PerfilNome;
            if (detalhes?.PerfilFotoUrl is not null) conexao.PerfilFotoUrl = detalhes.PerfilFotoUrl;
            conexao.Status = StatusConexao.Conectado;
            conexao.ConectadoEm = agora;
            log.LogInformation("Conexao {Id} conectada como {Numero}.", conexao.Id, conexao.Numero);
        }
        else
        {
            // close/connecting: nao esta conectada. Mantem numero e perfil (a tela mostra
            // "estava conectado como..."); e o status desconectado que acende o banner.
            conexao.Status = StatusConexao.Desconectado;
            conexao.DesconectadoEm = agora;
            log.LogInformation("Conexao {Id} caiu (state={State}).", conexao.Id, state);
        }

        conexao.StatusEm = agora;
        await db.SaveChangesAsync(ct);

        await painel.ConexaoMudouAsync(conexao.EmpresaId, new ConexaoPainel(
            conexao.Id, conexao.Status.ParaApi(),
            conexao.Numero, conexao.NumeroAnterior), ct);
    }

    // ==================================================================== mensagem
    private async Task ProcessarMensagemAsync(
        Conexao conexao, EventoEvolution ev, string payloadCru, CancellationToken ct)
    {
        var key = ev.Data?.Key;
        if (key?.Id is null || key.RemoteJid is null) return;

        // Grupo e broadcast nao sao atendimento um-a-um — ignorar.
        if (key.RemoteJid.Contains("@g.us") || key.RemoteJid.Contains("broadcast")) return;

        var quando = ev.Data?.MessageTimestamp is { } ts
            ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
            : relogio.GetUtcNow().UtcDateTime;

        // ===================== A EDICAO MUDA A ORIGINAL, E SO ISSO =====================
        // Ela chega pelo mesmo evento das mensagens e virava uma linha propria com o rotulo de
        // "nao suportada" — balao extra na thread e, por ser entrada, semaforo aceso e nao lida a
        // mais. Editar nao e escrever de novo: ninguem espera resposta por causa de um erro de
        // digitacao corrigido. Ver `EdicaoMensagem`.
        //
        // O tipo e conferido ANTES de abrir o JSON: edicao e rara, e este caminho roda em toda
        // mensagem — inclusive midia de payload grande.
        // ==============================================================================
        if (ev.Data?.MessageType == EdicaoMensagem.Tipo
            && EdicaoMensagem.Ler(payloadCru) is { } edicao)
        {
            await AplicarEdicaoAsync(conexao, edicao, key, quando, ct);
            return;
        }

        // ===================== O QUE NAO E CONTEUDO NAO VIRA LINHA (REC-2) =====================
        // Reacao, revogacao e distribuicao de chave chegam pelo mesmo evento das mensagens e nao
        // sao mensagem. Antes viravam linha VAZIA — balao branco na thread.
        //
        // ⚠️ E a reacao nao pode virar linha nem com rotulo: `AtualizarConversaAsync` acende
        // `aguardando_desde` em TODA entrada, entao um 👍 apareceria como "cliente esperando
        // resposta". Ver `ConteudoLegivel.NaoSaoConteudo`.
        // ==================================================================================
        if (ConteudoLegivel.EhRuido(ev.Data?.MessageType))
        {
            log.LogDebug("Evento {Tipo} ignorado: nao e conteudo de conversa.", ev.Data!.MessageType);
            return;
        }

        var telefone = CanonicalizadorTelefone.Canonicalizar(key.RemoteJid.Split('@')[0]);
        if (telefone.Length == 0) return;

        var entrada = !key.FromMe;
        // O modelo tipado responde pelos seis formatos que ele conhece; o resto sai do JSON cru
        // (template, botoes, localizacao, contato, enquete). Ver `ConteudoLegivel`.
        var texto = ev.Data?.Message?.Texto ?? ConteudoLegivel.Extrair(payloadCru);

        // ===================== DAQUI EM DIANTE, NAO IMPORTA O PROVEDOR =====================
        // Contato, conversa, anexo, gravacao, nota, semaforo, avisos e webhooks: tudo na
        // `RecepcaoMensagem`, a mesma que a Cloud API usa. Aqui so se traduz a Evolution.
        // ================================================================================
        var waId = key.Id;

        Func<CancellationToken, Task<MidiaDoProvedor>>? baixarMidia = null;
        if (EhMidia(ev.Data))
            baixarMidia = c => BaixarMidiaAsync(conexao, waId, payloadCru, c);

        var mensagem = new MensagemEntrante(
            WaMessageId: waId,
            Telefone: telefone,
            Entrada: entrada,
            Quando: quando,
            Texto: texto,
            NomePerfil: ev.Data?.PushName,
            CitadaWaId: ev.Data?.ContextInfo?.StanzaId,
            TipoParaRotulo: ev.Data?.MessageType ?? "desconhecido",
            PayloadRaw: payloadCru,
            BaixarMidia: baixarMidia);

        await Recepcao.ReceberAsync(conexao, mensagem, ct);
    }

    // ==================================================================== midia
    /// <summary>FIGURINHA ENTRA AQUI (REC-2), e nao como rotulo: ela e `image/webp`, e webp ja
    /// esta na whitelist desde o MID-1. Tratada como midia, aparece como imagem — que e o que
    /// ela e.</summary>
    private static bool EhMidia(DadosEvento? data) =>
        data?.Message?.Midia is not null
        || (data?.MessageType is { } t
            && (t.Contains("image", StringComparison.OrdinalIgnoreCase)
             || t.Contains("document", StringComparison.OrdinalIgnoreCase)
             || t.Contains("audio", StringComparison.OrdinalIgnoreCase)
             || t.Contains("video", StringComparison.OrdinalIgnoreCase)
             || t.Contains("sticker", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Baixa o anexo da Evolution. NUNCA lanca: a falha volta como `Erro`, que vai para
    /// `mensagens.erro` (REC-2). Validar e guardar e da `RecepcaoMensagem`.</summary>
    private async Task<MidiaDoProvedor> BaixarMidiaAsync(
        Conexao conexao, string waMessageId, string payloadCru, CancellationToken ct)
    {
        MidiaRecebida? midia;
        // O no `data` do webhook, cru: e o que a Evolution precisa para decodificar sem
        // consultar o banco dela. Extraido aqui, e nao remontado a partir do modelo tipado,
        // porque um campo que a gente nao mapeou (e ha varios) faria a decodificacao falhar.
        string mensagemJson;
        try
        {
            using var doc = JsonDocument.Parse(payloadCru);
            mensagemJson = doc.RootElement.GetProperty("data").GetRawText();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException)
        {
            log.LogWarning("Payload sem no `data` ao baixar a midia {Id}.", waMessageId);
            return new MidiaDoProvedor(null, "payload sem o no `data`");
        }

        try { midia = await whatsapp.ObterMidiaAsync(conexao.InstanceName, waMessageId, mensagemJson, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Nao foi possivel baixar a midia {Id} da Evolution.", waMessageId);
            return new MidiaDoProvedor(null, $"falha ao baixar da Evolution: {ex.Message}");
        }
        // NULO tambem e falha: a Evolution respondeu e nao trouxe o arquivo. E o caso dos tres
        // vazios encontrados em producao.
        if (midia == null) return new MidiaDoProvedor(null, "a Evolution nao devolveu o arquivo");

        return new MidiaDoProvedor(midia, null);
    }

    // ==================================================================== ack
    /// <summary>ACK de entrega/leitura. O ack numerico e a FONTE DE VERDADE e SO AVANCA: os
    /// webhooks chegam fora de ordem, entao um DELIVERY_ACK atrasado nao pode sobrescrever um
    /// READ que ja chegou.</summary>
    private async Task ProcessarAckAsync(Conexao conexao, EventoEvolution ev, CancellationToken ct)
    {
        // O formato medido e o PLANO (`data.keyId`); `data.key.id` fica aceito, que e o que havia
        // antes. Ver `DadosEvento.KeyId`.
        var waId = ev.Data?.KeyId ?? ev.Data?.Key?.Id;
        var status = ev.Data?.Status;
        if (waId is null || status is null) return;

        var ack = AckDe(status);
        if (ack is null) return;

        var afetadas = await db.Database.ExecuteSqlRawAsync("""
            UPDATE mensagens
               SET ack = {2}, ack_em = {3}
             WHERE empresa_id = {0} AND wa_message_id = {1}
               AND (ack IS NULL OR ack < {2})
            """, [conexao.EmpresaId, waId, ack.Value, relogio.GetUtcNow().UtcDateTime], ct);

        if (afetadas == 0) return;   // ACK repetido ou fora de ordem: ignorado, sem barulho

        // Depois do `return` de cima de proposito: a primeira confirmacao que avanca ja traz o LID,
        // e as repetidas nao precisam pagar outra consulta.
        await GuardarLidAsync(conexao, waId, ev.Data?.RemoteJid ?? ev.Data?.Key?.RemoteJid, ct);

        var mensagemId = await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == conexao.EmpresaId && m.WaMessageId == waId)
            .Select(m => m.Id).FirstOrDefaultAsync(ct);

        if (mensagemId != 0)
            await painel.StatusMensagemAsync(conexao.EmpresaId, mensagemId, ack.Value, ct);
    }

    /// <summary>O LID do contato, tirado da confirmacao de entrega — a unica fonte dele (ver
    /// `Contato.Lid`). So escreve quando muda, e nunca em contato anonimizado.</summary>
    private async Task GuardarLidAsync(
        Conexao conexao, string waId, string? remoteJid, CancellationToken ct)
    {
        if (EdicaoMensagem.LidDe(remoteJid) is not { } lid) return;

        await db.Database.ExecuteSqlRawAsync("""
            UPDATE contatos c
               SET lid = {2}
              FROM mensagens m
             WHERE m.empresa_id = {0} AND m.wa_message_id = {1}
               AND c.id = m.contato_id AND c.empresa_id = m.empresa_id
               AND c.anonimizado_em IS NULL
               AND c.lid IS DISTINCT FROM {2}
            """, [conexao.EmpresaId, waId, lid], ct);
    }

    // ==================================================================== edicao
    /// <summary>Aplica a edicao na original: TROCA O TEXTO quando consegue abri-la, e so marca
    /// quando nao consegue. O antigo vai para `texto_original` (ver `EdicaoMensagem`).
    ///
    /// Os candidatos a autor: na ENTRADA, o LID do contato — o que abre — e o JID de telefone da
    /// chave; na SAIDA, o vendedor editando do proprio celular, o numero da conexao. O NOSSO LID
    /// nao e conhecido, entao a edicao feita pelo vendedor tende a ficar so marcada.
    ///
    /// A thread aberta recarrega NA HORA: o aviso e o mesmo das confirmacoes de entrega, com o
    /// `ack` nulo. Sem ele, o texto novo so aparecia quando chegasse a mensagem seguinte.
    ///
    /// SO AVANCA, como o ACK: a reentrega do webhook e a edicao fora de ordem nao desfazem a
    /// ultima.</summary>
    private async Task AplicarEdicaoAsync(
        Conexao conexao, EdicaoMensagem.Edicao edicao, ChaveMensagem chave, DateTime quando,
        CancellationToken ct)
    {
        var original = await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == conexao.EmpresaId && m.WaMessageId == edicao.Alvo)
            .Select(m => new { m.Id, m.ConversaId, m.Direcao, m.CriadoEm, m.EditadaEm, m.PayloadRaw, m.Contato.Lid })
            .FirstOrDefaultAsync(ct);

        if (original is null || original.EditadaEm >= quando)
        {
            // Reentrega, ou a original e anterior ao Nexora. Nos dois casos nao ha o que mudar.
            log.LogDebug("Edicao de {WaId} sem efeito: reentrega ou original desconhecida.", edicao.Alvo);
            return;
        }

        string?[] autores = original.Direcao == DirecaoMensagem.Entrada
            ? [original.Lid, chave.RemoteJid]
            : [conexao.Numero is { } numero ? $"{numero}@s.whatsapp.net" : null];

        var novo = EdicaoMensagem.SegredoDa(original.PayloadRaw) is { } segredo
            ? EdicaoMensagem.Decifrar(edicao, segredo, autores)
            : null;

        var linha = db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.Id == original.Id && (m.EditadaEm == null || m.EditadaEm < quando));

        if (novo is null)
        {
            log.LogInformation("Edicao de {WaId} marcada sem o texto novo: {Motivo}.", edicao.Alvo,
                original.Direcao == DirecaoMensagem.Entrada && original.Lid is null
                    ? "o contato ainda nao tem LID"
                    : "nenhum candidato abriu");
            if (await linha.ExecuteUpdateAsync(s => s.SetProperty(m => m.EditadaEm, quando), ct) > 0)
                await painel.StatusMensagemAsync(conexao.EmpresaId, original.Id, null, ct);
            return;
        }

        var afetadas = await linha.ExecuteUpdateAsync(s => s
            .SetProperty(m => m.TextoOriginal, m => m.TextoOriginal ?? m.Texto)
            .SetProperty(m => m.Texto, novo)
            .SetProperty(m => m.EditadaEm, quando), ct);

        if (afetadas == 0) return;   // outra entrega da mesma edicao chegou primeiro

        // A previa da caixa e o texto da ULTIMA mensagem: se for esta, ela muda junto.
        var previa = PreviaTexto.Cortar(novo);
        await db.Conversas.IgnoreQueryFilters()
            .Where(c => c.Id == original.ConversaId && c.UltimaMensagemEm == original.CriadoEm)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UltimaMensagemPrevia, previa), ct);

        await painel.StatusMensagemAsync(conexao.EmpresaId, original.Id, null, ct);
    }

    private static short? AckDe(string status) => status.ToUpperInvariant() switch
    {
        "ERROR" => 0,
        "PENDING" => 1,
        "SERVER_ACK" => 2,
        "DELIVERY_ACK" => 3,
        "READ" => 4,
        "PLAYED" => 4,
        _ => null
    };
}
