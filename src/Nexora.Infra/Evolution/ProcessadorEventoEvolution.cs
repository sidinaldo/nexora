using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Captacao;
using Nexora.Core.Entidades;
using Nexora.Core.Webhooks;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

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
    TimeProvider relogio,
    ILogger<ProcessadorEventoEvolution> log) : IProcessadorWebhookWhatsApp
{
    /// <summary>Midia recebida depois de baixada. Salvo=false + Recusada=true = fora da
    /// whitelist ou grande demais; Salvo=false sem Recusada = nao deu para baixar.</summary>
    private sealed record MidiaBaixada(
        bool Salvo, bool Recusada, string? Chave, string? Mime, string? Nome, int Tamanho, TipoMidia Tipo);


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

        var telefone = CanonicalizadorTelefone.Canonicalizar(key.RemoteJid.Split('@')[0]);
        if (telefone.Length == 0) return;

        var entrada = !key.FromMe;
        var quando = ev.Data?.MessageTimestamp is { } ts
            ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime
            : relogio.GetUtcNow().UtcDateTime;
        var texto = ev.Data?.Message?.Texto;

        // TUDO numa transacao: contato, conversa, mensagem e a atualizacao de aguardando_desde
        // tem que cair juntos. Se a mensagem entra e o aguardando_desde nao e gravado, o
        // semaforo mente e ninguem percebe — e o modo de falha mais caro deste desenho.
        //
        // Transacao propria so quando nao ha uma em curso (abrir aninhada lanca). Em producao
        // sempre ha de abrir; o caso do chamador ja ter a dele e o teste.
        var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {

        // ============ CASAR O NUMERO COM O CONTATO ============
        // Aqui e onde o sistema silenciosamente para de funcionar se a normalizacao divergir: o
        // cadastro digita "(84) 98888-7777" (sem DDI) e o WhatsApp entrega
        // "5584988887777@s.whatsapp.net" (com DDI). As VARIANTES cobrem o nono digito, que o
        // WhatsApp as vezes omite.
            var variantes = CanonicalizadorTelefone.Variantes(telefone);
            var contato = await db.Contatos.IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.EmpresaId == conexao.EmpresaId && variantes.Contains(c.Telefone), ct);

            var contatoNovo = false;
            if (contato is null)
            {
                // ============ CAPTURA DE LEAD ============
                // No Recupera, numero fora da carteira so gerava um aviso "sem cadastro" para o
                // operador decidir. Num CRM de vendas, mensagem de desconhecido E um lead novo:
                // criar o contato e o comportamento certo, e sem isso o produto nao funciona.
                contato = await CriarContatoAsync(
                    conexao.EmpresaId, telefone, ev.Data?.PushName, entrada ? texto : null, ct);
                if (contato is null) return;   // empresa sem etapa: ja logado
                contatoNovo = true;
            }

            // ATRIBUICAO SO NA CRIACAO. Contato que ja existe NAO tem a origem reescrita: a
            // primeira origem e a verdadeira, e sobrescrever destroi o relatorio — o cliente que
            // voltou pelo panfleto de julho continua sendo o lead do Instagram de marco.

            var (conversa, conversaNova) = await ObterOuCriarConversaAsync(conexao, contato, quando, ct);

            // Midia? Baixa da Evolution, valida (whitelist/tamanho) e guarda.
            var midia = EhMidia(ev.Data)
                ? await ReceberMidiaAsync(conexao, key.Id!, payloadCru, ct)
                : null;

            var textoMensagem = midia?.Recusada == true
                ? "[anexo recusado — tipo nao permitido ou tamanho acima do limite]"
                : texto;

            var mensagemId = await InserirMensagemAsync(
                conexao, contato, conversa, key, entrada, textoMensagem, ev, payloadCru, quando, midia, ct);

            if (mensagemId is null)
            {
                // Ja tinhamos esta mensagem: webhook REENTREGUE, ou ECO do proprio envio (a
                // Evolution devolve por webhook o que acabamos de mandar). Nos dois casos nao ha
                // o que fazer — e NAO se pode tocar a conversa: a reentrega zeraria o
                // aguardando_desde ou inflaria o contador de nao lidas.
                if (tx is not null) await tx.CommitAsync(ct);   // preserva o contato, se criado
                return;
            }

            await AtualizarConversaAsync(conversa, entrada, textoMensagem, quando, ct);
            if (tx is not null) await tx.CommitAsync(ct);

            // Notificacoes DEPOIS do commit: se o painel receber o evento antes de a transacao
            // fechar, a tela consulta e nao encontra a linha.
            if (contatoNovo)
                await painel.ContatoCriadoAsync(conexao.EmpresaId,
                    new ContatoPainel(contato.Id, contato.Nome, contato.Telefone, contato.EtapaId), ct);

            if (conversaNova)
                await painel.ConversaAbertaAsync(conexao.EmpresaId,
                    new ConversaPainel(conversa.Id, contato.Id, contato.Nome, contato.Telefone), ct);

            await painel.MensagemRecebidaAsync(conexao.EmpresaId, new MensagemPainel(
                mensagemId.Value, conversa.Id, contato.Id, contato.Nome,
                Previa(textoMensagem), entrada ? "entrada" : "saida", quando), ct);

            // ===================== WEBHOOK DE SAIDA (INT-3) =====================
            // Depois do commit e depois do dedupe, pelo mesmo motivo das notificacoes do painel: a
            // reentrega do webhook da Evolution nao pode virar evento duplicado no sistema do
            // cliente. O `return` la em cima (mensagem ja existente) passa por fora daqui.
            //
            // `lead.criado` sai TAMBEM por aqui: o lead que chega pelo WhatsApp e o caminho de
            // maior volume, e nao ter o evento aqui faria o cliente ver so os contatos digitados a
            // mao chegarem no ERP dele.
            //
            // SO na ENTRADA para a mensagem: `fromMe=true` e o eco do que NOS mandamos, e avisar o
            // sistema do cliente de que "chegou uma mensagem" que foi ele mesmo quem mandou e o
            // comeco de um laco de integracao.
            if (contatoNovo)
                await eventos.PublicarContatoAsync(EventoWebhook.LeadCriado, contato, ct: ct);

            if (entrada)
                await eventos.PublicarMensagemAsync(
                    conexao.EmpresaId, mensagemId.Value, contato.Id, conversa.Id,
                    textoMensagem, contato.Nome, contato.Telefone, quando, ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    /// <summary>Cria o contato capturado do WhatsApp: nome = pushName (ou o telefone formatado,
    /// porque a coluna e NOT NULL), etapa de MENOR ordem (Novo Lead) e SEM responsavel — cai em
    /// "Nao atribuidas" para alguem assumir.
    ///
    /// A ORIGEM sai do codigo de canal no texto (INT-2), quando houver. Sem codigo, `whatsapp` —
    /// como sempre foi.</summary>
    private async Task<Contato?> CriarContatoAsync(
        long empresaId, string telefone, string? pushName, string? texto, CancellationToken ct)
    {
        var etapaId = await db.EtapasFunil.IgnoreQueryFilters()
            .Where(e => e.EmpresaId == empresaId)
            .OrderBy(e => e.Ordem)
            .Select(e => (long?)e.Id)
            .FirstOrDefaultAsync(ct);

        if (etapaId is null)
        {
            // Empresa sem funil nao deveria existir (o cadastro semeia as 5 etapas), mas se
            // acontecer e melhor perder a mensagem com log alto do que estourar no webhook e
            // entrar em loop de reentrega.
            log.LogError("Empresa {Id} sem etapa de funil — contato de {Tel} nao pode ser criado.",
                empresaId, telefone);
            return null;
        }

        var nome = string.IsNullOrWhiteSpace(pushName)
            ? CanonicalizadorTelefone.Formatar(telefone)
            : pushName!.Trim();

        var canal = await CanalDoTextoAsync(empresaId, texto, ct);

        var contato = new Contato
        {
            EmpresaId = empresaId,
            Nome = nome.Length <= 120 ? nome : nome[..120],
            Telefone = telefone,
            Origem = canal?.Origem ?? OrigemLead.Whatsapp,
            OrigemDetalhe = canal?.Nome,
            EtapaId = etapaId.Value,
            ResponsavelId = null,
            OrdemKanban = 0m
        };
        db.Contatos.Add(contato);

        // O contador sobe JUNTO com o contato, na mesma transacao e no mesmo SaveChanges. Separar
        // deixaria o par "contato criado / lead contado" divergir na primeira falha parcial, e o
        // numero da tela e o que o cliente usa para decidir se o panfleto valeu a pena.
        if (canal is not null) canal.LeadsRecebidos += 1;

        await db.SaveChangesAsync(ct);

        if (canal is null)
            log.LogInformation("Contato {Id} criado automaticamente a partir do WhatsApp ({Tel}).",
                contato.Id, telefone);
        else
            log.LogInformation(
                "Contato {Id} criado pelo canal '{Canal}' (codigo {Codigo}, telefone {Tel}).",
                contato.Id, canal.Nome, canal.Codigo, telefone);

        return contato;
    }

    /// <summary>O canal de captacao cujo codigo aparece no texto da mensagem, ou null.
    ///
    /// ===================== NUNCA FALHA, E NUNCA ADIVINHA =====================
    /// Tres decisoes que este metodo materializa:
    ///
    ///   • SEM codigo, ou com codigo que nao existe, devolve null e o contato entra como
    ///     `whatsapp`. Nao ha erro, nao ha log de alerta: a pessoa apagar o texto antes de mandar
    ///     e o caso ESPERADO, nao uma falha;
    ///   • codigo de OUTRA empresa nao existe daqui — o filtro por `empresaId` e explicito, e o
    ///     tenant veio do `instance_name` da conexao. Nao ha caminho em que o canal do concorrente
    ///     carimbe um lead nosso;
    ///   • nao ha nenhuma tentativa de inferir origem por proximidade de horario, campanha ativa
    ///     ou qualquer outro sinal. Atribuicao errada e pior que atribuicao ausente: ela entra no
    ///     relatorio parecendo verdade, e o cliente decide onde gastar em cima dela.
    ///
    /// Canal DESATIVADO nao atribui. O material impresso continua no mundo e o lead continua
    /// entrando — so que sem carimbo, porque desativar e como o cliente diz "essa campanha
    /// acabou".
    /// =========================================================================</summary>
    private async Task<CanalCaptacao?> CanalDoTextoAsync(
        long empresaId, string? texto, CancellationToken ct)
    {
        var candidatos = CodigoCanal.Extrair(texto);
        if (candidatos.Count == 0) return null;

        // IgnoreQueryFilters + filtro explicito: o webhook roda sem tenant no contexto, e sem isso
        // a consulta voltaria vazia em silencio — nenhum canal atribuiria nada, para sempre.
        var canais = await db.CanaisCaptacao.IgnoreQueryFilters()
            .Where(c => c.EmpresaId == empresaId && c.Ativo && candidatos.Contains(c.Codigo))
            .ToListAsync(ct);

        if (canais.Count == 0) return null;

        // A ordem do TEXTO decide, nao a do banco: se a pessoa colou duas frases (encaminhou a
        // mensagem de um amigo e escreveu a dela), o primeiro codigo e o do caminho que ela
        // percorreu. Deixar o banco escolher tornaria a atribuicao dependente da ordem fisica das
        // linhas, que ninguem controla.
        foreach (var codigo in candidatos)
            if (canais.FirstOrDefault(c => c.Codigo == codigo) is { } achado)
                return achado;

        return null;
    }

    /// <summary>A conversa do contato. 1:1 na fase 1 (uq_conversas_contato) — substitui o
    /// AbrirOuObterTicketAsync do Recupera, que precisava adivinhar de qual divida o devedor
    /// estava falando.</summary>
    private async Task<(Conversa Conversa, bool Nova)> ObterOuCriarConversaAsync(
        Conexao conexao, Contato contato, DateTime quando, CancellationToken ct)
    {
        var existente = await db.Conversas.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.ContatoId == contato.Id, ct);
        if (existente is not null) return (existente, false);

        var conversa = new Conversa
        {
            EmpresaId = conexao.EmpresaId,
            ContatoId = contato.Id,
            ConexaoId = conexao.Id,
            Status = StatusConversa.Aberta,
            UltimaMensagemEm = quando,
            ResponsavelId = null
        };
        db.Conversas.Add(conversa);
        await db.SaveChangesAsync(ct);
        return (conversa, true);
    }

    /// <summary>INSERT ... ON CONFLICT DO NOTHING contra uq_msg_wa_id. Devolve NULL quando a
    /// mensagem ja existia — o que cobre DOIS casos distintos: o webhook reentregue e o eco do
    /// proprio envio.
    ///
    /// SQL cru de proposito: o EF nao expressa ON CONFLICT, e a alternativa (capturar
    /// DbUpdateException) envenena o ChangeTracker numa operacao que barra de proposito.</summary>
    private async Task<long?> InserirMensagemAsync(
        Conexao conexao, Contato contato, Conversa conversa, ChaveMensagem key, bool entrada,
        string? texto, EventoEvolution ev, string payloadCru, DateTime quando,
        MidiaBaixada? midia, CancellationToken ct)
    {
        var salva = midia is { Salvo: true };

        var ids = await db.Database.SqlQueryRaw<long>("""
            INSERT INTO mensagens (
                empresa_id, conversa_id, contato_id, conexao_id, instance_name,
                direcao, wa_message_id, texto,
                tipo_midia, midia_chave, midia_mime, midia_nome, midia_bytes,
                data_disparo, reservado_em, enviada_em, recebida_em, payload_raw, criado_em,
                recuperada_em)
            VALUES (
                {0}, {1}, {2}, {3}, {4},
                CAST({5} AS direcao_mensagem_enum), {6}, {7},
                CAST({8} AS tipo_midia_enum), {9}, {10}, {11}, {12},
                {13}, {14}, {15}, {16}, CAST({17} AS jsonb), {14},
                {18})
            ON CONFLICT DO NOTHING
            RETURNING id AS "Value"
            """,
            conexao.EmpresaId, conversa.Id, contato.Id, conexao.Id, conexao.InstanceName,
            entrada ? "entrada" : "saida",
            (object?)key.Id ?? DBNull.Value,
            (object?)texto ?? DBNull.Value,
            (salva ? midia!.Tipo : TipoMidia.Nenhum).ToString().ToLowerInvariant(),
            (object?)(salva ? midia!.Chave : null) ?? DBNull.Value,
            (object?)(salva ? midia!.Mime : null) ?? DBNull.Value,
            (object?)(salva ? midia!.Nome : null) ?? DBNull.Value,
            (object?)(salva ? midia!.Tamanho : (int?)null) ?? DBNull.Value,
            // data_disparo so faz sentido em SAIDA (ck_msg_data_disparo). Mensagem que chega
            // pelo webhook com fromMe=true foi mandada pelo CELULAR: nao passou pela outbox,
            // entao a data-alvo e o proprio dia.
            entrada ? (object)DBNull.Value : DateOnly.FromDateTime(quando),
            quando,
            entrada ? (object)DBNull.Value : quando,   // enviada_em
            entrada ? quando : (object)DBNull.Value,   // recebida_em
            payloadCru,
            // Carimbo de atraso (REC-1). So na ENTRADA: `fromMe` e o eco do que NOS mandamos, e
            // "mensagem recuperada" e sobre o que o cliente escreveu e ninguem viu.
            entrada
                ? (object?)JanelaRecuperacao.CarimboDe(quando, relogio.GetUtcNow().UtcDateTime)
                  ?? DBNull.Value
                : DBNull.Value).ToListAsync(ct);

        return ids.Count > 0 ? ids[0] : null;
    }

    /// <summary>===== A MANUTENCAO DE aguardando_desde =====
    ///
    /// Nao existe no Recupera (la o equivalente era calculado com max(id) por conversa a cada
    /// leitura). Aqui a coluna e materializada, e e o coracao do semaforo, do Meu Dia e de um
    /// dos quatro numeros do dashboard.
    ///
    /// ENTRADA: se ainda nao havia nada esperando, marca o instante. Se JA havia, NAO
    /// sobrescreve — o que importa e desde quando o contato espera resposta, nao qual foi a
    /// ultima mensagem que ele mandou. Sobrescrever faria o semaforo "rejuvenescer" a cada
    /// cobranca do cliente, que e exatamente o contrario do que ele deve mostrar.
    ///
    /// SAIDA: respondemos, entao ninguem espera mais — zera, e zera tambem o nao lidas.</summary>
    private async Task AtualizarConversaAsync(
        Conversa conversa, bool entrada, string? texto, DateTime quando, CancellationToken ct)
    {
        // ===================== MENSAGEM ATRASADA NÃO É MENSAGEM DE AGORA (REC-1) =====================
        // Este método assumia que `quando` é sempre o instante mais recente da conversa. Isso vale
        // enquanto tudo chega em tempo real — e deixa de valer no dia em que a entrega atrasa, que
        // acontece SOZINHO: a Evolution reentrega webhook recusado por até ~20 minutos (10
        // tentativas, backoff de 5s a 300s), e o WhatsApp enfileira o que chegou com a instância
        // fora do ar. Nos dois casos a mensagem entra aqui com o timestamp ORIGINAL.
        //
        // Sem os guardas abaixo, uma mensagem de ontem processada hoje:
        //   • puxava `ultima_mensagem_em` para trás, e a conversa descia na caixa de entrada;
        //   • sobrescrevia a prévia com um texto velho;
        //   • reacendia o semáforo de uma conversa já respondida.
        //
        // O CRITÉRIO é a ordem no tempo, não a ordem de chegada.
        // ============================================================================================
        var maisRecente = quando >= conversa.UltimaMensagemEm;

        // Existe resposta NOSSA mais nova que esta mensagem? Então ela já foi atendida — não pode
        // reabrir espera nem somar não lida, por mais que só agora tenha entrado no banco.
        var respondidaDepois = !maisRecente
            && conversa.UltimaMensagemDirecao == DirecaoMensagem.Saida;

        if (entrada)
        {
            if (!respondidaDepois)
            {
                // O MENOR dos dois, não o primeiro a ser gravado: "aguardando desde" é o começo da
                // espera. Chegando fora de ordem, a mensagem mais antiga é que define o vermelho —
                // `??=` guardaria a que foi processada primeiro, que é acidente de entrega.
                conversa.AguardandoDesde =
                    conversa.AguardandoDesde is { } desde && desde < quando ? desde : quando;

                conversa.NaoLidas += 1;
            }
        }
        else if (maisRecente)
        {
            // Zerar só quando esta É a última palavra. Uma saída antiga que só agora foi registrada
            // não pode apagar a espera de uma entrada posterior a ela.
            conversa.AguardandoDesde = null;
            conversa.NaoLidas = 0;
        }

        if (maisRecente)
        {
            conversa.UltimaMensagemEm = quando;
            conversa.UltimaMensagemDirecao = entrada ? DirecaoMensagem.Entrada : DirecaoMensagem.Saida;
            conversa.UltimaMensagemPrevia = Previa(texto);
        }

        await db.SaveChangesAsync(ct);

        // ===== O TEMPO ATE O VALOR =====
        // A PRIMEIRA mensagem de entrada da empresa carimba `primeira_mensagem_em`. A diferenca
        // para `criado_em` e o intervalo entre assinar e o produto funcionar de verdade — a
        // metrica que prevê abandono melhor que qualquer outra.
        //
        // `WHERE primeira_mensagem_em IS NULL` faz o UPDATE ser no-op da segunda mensagem em
        // diante: e a mesma disciplina do `??=` acima, mas aplicada no SQL porque este caminho
        // roda como JOB (sem tenant) e a linha da empresa nao esta sendo rastreada.
        // `> quando` além do IS NULL (REC-1): mensagem atrasada pode ser a PRIMEIRA de verdade,
        // registrada depois de outra mais nova. Sem isso, "minutos até a primeira mensagem" mediria
        // a ordem de entrega em vez do tempo até o produto funcionar. Continua idempotente: a
        // segunda passada da mesma mensagem não satisfaz nenhuma das duas condições.
        if (entrada)
            await db.Empresas.IgnoreQueryFilters()
                .Where(e => e.Id == conversa.EmpresaId
                         && (e.PrimeiraMensagemEm == null || e.PrimeiraMensagemEm > quando))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.PrimeiraMensagemEm, quando), ct);
    }

    /// <summary>Corte por CLUSTER DE GRAFEMA (MID-1). Cortar por unidade de código partia emoji
    /// composto ao meio e a lista da caixa exibia o losango de interrogação. Ver PreviaTexto.</summary>
    private static string? Previa(string? texto) => PreviaTexto.Cortar(texto);

    // ==================================================================== midia
    private static bool EhMidia(DadosEvento? data) =>
        data?.Message?.Midia is not null
        || (data?.MessageType is { } t
            && (t.Contains("image", StringComparison.OrdinalIgnoreCase)
             || t.Contains("document", StringComparison.OrdinalIgnoreCase)
             || t.Contains("audio", StringComparison.OrdinalIgnoreCase)
             || t.Contains("video", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Baixa a midia da Evolution, valida e guarda. NUNCA lanca (o webhook precisa
    /// responder 2xx): falha vira mensagem sem anexo, com log.</summary>
    private async Task<MidiaBaixada> ReceberMidiaAsync(
        Conexao conexao, string waMessageId, string payloadCru, CancellationToken ct)
    {
        var nada = new MidiaBaixada(false, false, null, null, null, 0, TipoMidia.Nenhum);

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
            return new MidiaBaixada(false, false, null, null, null, 0, TipoMidia.Nenhum);
        }

        try { midia = await whatsapp.ObterMidiaAsync(conexao.InstanceName, waMessageId, mensagemJson, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Nao foi possivel baixar a midia {Id} da Evolution.", waMessageId);
            return nada;
        }
        if (midia is null) return nada;

        if (!ValidadorMidia.MimePermitido(midia.MimeType))
        {
            log.LogInformation("Midia de tipo nao permitido ({Mime}) — recusada.", midia.MimeType);
            return nada with { Recusada = true };
        }

        var b64 = midia.Base64;
        var virgula = b64.IndexOf(',');
        if (b64.StartsWith("data:") && virgula > 0) b64 = b64[(virgula + 1)..];   // tira prefixo data:

        byte[] conteudo;
        try { conteudo = Convert.FromBase64String(b64); }
        catch (FormatException) { return nada; }

        if (!ValidadorMidia.TamanhoOk(conteudo.LongLength))
        {
            log.LogInformation("Midia de {Bytes} bytes acima do limite — recusada.", conteudo.LongLength);
            return nada with { Recusada = true };
        }

        var mime = ValidadorMidia.Normalizar(midia.MimeType)!;

        // Chave DETERMINISTICA pelo wa_message_id: o arquivo e gravado ANTES do dedupe do
        // INSERT, e a Evolution reentrega o webhook ate receber 2xx. Com chave aleatoria, cada
        // reentrega deixaria um objeto orfao (sem linha em mensagens, nunca expurgado).
        // Determinista => a reentrega sobrescreve o mesmo objeto e a linha da 1a entrega segue
        // apontando para ele.
        var idSafe = new string([.. waMessageId.Where(char.IsLetterOrDigit)]);
        if (idSafe.Length == 0) idSafe = Guid.NewGuid().ToString("N");
        var chave = $"emp-{conexao.EmpresaId}/{idSafe}.{ValidadorMidia.ExtensaoDe(mime)}";

        try { await armazenamento.SalvarAsync(conteudo, chave, ct); }
        catch (Exception ex)
        {
            log.LogError(ex, "Falha ao gravar a midia {Chave}.", chave);
            return nada;
        }

        var nome = string.IsNullOrWhiteSpace(midia.FileName)
            ? $"anexo.{ValidadorMidia.ExtensaoDe(mime)}"
            : midia.FileName!;

        return new MidiaBaixada(true, false, chave, mime,
            nome.Length <= 200 ? nome : nome[..200],
            conteudo.Length, ValidadorMidia.TipoDe(mime));
    }

    // ==================================================================== ack
    /// <summary>ACK de entrega/leitura. O ack numerico e a FONTE DE VERDADE e SO AVANCA: os
    /// webhooks chegam fora de ordem, entao um DELIVERY_ACK atrasado nao pode sobrescrever um
    /// READ que ja chegou.</summary>
    private async Task ProcessarAckAsync(Conexao conexao, EventoEvolution ev, CancellationToken ct)
    {
        var waId = ev.Data?.Key?.Id;
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

        var mensagemId = await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.EmpresaId == conexao.EmpresaId && m.WaMessageId == waId)
            .Select(m => m.Id).FirstOrDefaultAsync(ct);

        if (mensagemId != 0)
            await painel.StatusMensagemAsync(conexao.EmpresaId, mensagemId, ack.Value, ct);
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
