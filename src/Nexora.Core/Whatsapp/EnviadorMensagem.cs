using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;

namespace Nexora.Core.Whatsapp;

public class OpcoesEnvio
{
    /// <summary>Pausa entre disparos AUTOMATICOS. O lembrete manda em lote pela mesma instancia
    /// do WhatsApp, e mandar tudo de uma vez e o jeito classico de ter o numero bloqueado — o
    /// Nexora roda em rota nao-oficial, onde banimento e risco real.
    ///
    /// NAO vale para resposta manual do vendedor: ele digita no ritmo dele.</summary>
    public TimeSpan IntervaloEntreEnvios { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Quantos dias para tras a drenagem procura reservas que nunca sairam. Passado
    /// disso a linha e marcada como EXPIRADA (ver EnviadorMensagem.ExpirarVencidasAsync).</summary>
    public int JanelaReenvioDias { get; set; } = 3;
}

/// <summary>Dados de que o envio precisa. Interface no Core, EF/SQL na Infra.</summary>
public interface IDadosMensagem
{
    /// <summary>RESERVA o disparo do lembrete: INSERT ... ON CONFLICT DO NOTHING RETURNING id.
    /// NULL = o banco barrou (este lembrete ja gerou mensagem, via uq_msg_lembrete).</summary>
    Task<long?> ReservarLembreteAsync(Mensagem reserva, CancellationToken ct);

    /// <summary>RESERVA o envio da pesquisa de NPS: INSERT ... ON CONFLICT DO NOTHING RETURNING
    /// id. NULL = o banco barrou (esta venda ja gerou pesquisa enviada, via uq_msg_nps).
    ///
    /// ⚠️ SEPARADO DO LEMBRETE porque a ANCORA e outra: lembrete deduplica por `lembrete_id`, a
    /// pesquisa por `negociacao_id` com `tipo_automacao = 'nps'`. Um metodo so, com a ancora vindo
    /// por parametro, esconderia qual invariante esta de guarda em cada chamada.</summary>
    Task<long?> ReservarNpsAsync(Mensagem reserva, CancellationToken ct);

    /// <summary>A linha da PERGUNTA de NPS desta venda, se ja existir — a que `uq_msg_nps` guarda.
    /// Nulo quando nao ha. E o que deixa a rodada saber se a reserva barrada de fato SAIU.</summary>
    Task<Mensagem?> PerguntaNpsDaVendaAsync(long empresaId, long negociacaoId, CancellationToken ct);

    /// <summary>Grava uma mensagem MANUAL (resposta do vendedor na conversa). Nao passa por
    /// invariante nenhuma: lembrete_id fica NULL de proposito, entao o dedupe por lembrete nao
    /// se aplica. Dentro de uma conversa viva o vendedor responde quantas vezes precisar.</summary>
    Task<long> GravarManualAsync(Mensagem mensagem, CancellationToken ct);

    Task ConfirmarEnvioAsync(long mensagemId, string waMessageId, CancellationToken ct);

    /// <summary>Registra a falha E incrementa o contador de tentativas. A linha FICA.</summary>
    Task RegistrarFalhaAsync(long mensagemId, string erro, CancellationToken ct);

    /// <summary>Reservas que nunca foram despachadas — a Evolution caiu, a conexao estava fora,
    /// ou o POST foi ADIADO por estar fora da janela. A linha existe (entao a invariante segue
    /// valendo e nao ha risco de duplicar), mas `enviada_em` ainda e NULL.</summary>
    Task<IReadOnlyList<Mensagem>> PendentesAsync(long empresaId, DateOnly desde, CancellationToken ct);

    /// <summary>Marca como EXPIRADA toda reserva anterior ao limite. Devolve quantas.</summary>
    Task<int> ExpirarVencidasAsync(long empresaId, DateOnly limite, CancellationToken ct);

    /// <summary>A empresa é de DEMONSTRAÇÃO? Consultado no caminho de envio, antes de postar.
    ///
    /// Uma leitura por chave primária por disparo. Vale o custo: o que ela impede é o tenant de
    /// demonstração mandar WhatsApp para número de estranho.</summary>
    Task<bool> EhDemonstracaoAsync(long empresaId, CancellationToken ct);
}

public enum ResultadoEnvio
{
    Enviada,

    /// <summary>Uma invariante do banco barrou. Nao e erro — e o sistema funcionando.</summary>
    Barrada,

    Falhou,

    /// <summary>Reservada, mas o POST foi ADIADO (fora da janela, ou conexao caida). A linha
    /// fica pendente (enviada_em NULL) e a proxima drenagem a posta.</summary>
    Adiada
}

/// <summary>O DONO UNICO DO PROTOCOLO DE ENVIO.
///
/// ======================= O PROTOCOLO (nao inverter) =======================
/// GRAVA no banco  ->  SO ENTAO chama o WhatsApp  ->  confirma (ou registra a falha).
///
/// Se disparasse ANTES de gravar, um crash entre as duas etapas faria o contato receber a
/// mesma mensagem de novo na proxima rodada — e nao haveria como saber.
///
/// Na FALHA a linha FICA, com o erro gravado. Apagar liberaria a invariante de dedupe, e um
/// POST que na verdade chegou (mas deu timeout na resposta) viraria mensagem duplicada no
/// reenvio. O reenvio reaproveita a MESMA linha.
///
/// Para o LEMBRETE, o "grava" e um INSERT ... ON CONFLICT DO NOTHING contra uq_msg_lembrete.
/// Sem id de volta = barrado, pula. Para a RESPOSTA MANUAL, e um INSERT normal — invariante
/// nenhuma se aplica.
/// ==========================================================================
///
/// Esta classe existe porque, no Recupera, o protocolo estava DUPLICADO: uma copia no motor da
/// regua e outra no controller da caixa de entrada. Duas copias da mesma invariante divergem, e
/// o bug resultante (mensagem duplicada para o cliente) e invisivel ate ele reclamar. Quem
/// precisar mandar mensagem daqui pra frente passa por aqui.</summary>
public class EnviadorMensagem(
    IDadosMensagem dados,
    IClienteWhatsApp whatsapp,
    OpcoesEnvio opcoes,
    TimeProvider relogio,
    ILogger<EnviadorMensagem> log)
{
    // O DESTINO viaja como parametro, nao dentro da entidade: diferente do Recupera, a tabela
    // `mensagens` do Nexora nao guarda remote_jid — o telefone vive em `contatos`, fonte unica.
    // Quem chama ja tem o contato em maos.

    /// <summary>Disparo do lembrete automatico: reserva e posta.</summary>
    public async Task<ResultadoEnvio> EnviarLembreteAsync(
        Mensagem reserva, string telefone, CancellationToken ct)
    {
        var id = await dados.ReservarLembreteAsync(reserva, ct);
        if (id is null) return ResultadoEnvio.Barrada;

        reserva.Id = id.Value;
        return await DispararAsync(
                reserva.InstanceName, telefone, reserva.Texto ?? "", id.Value, reserva.EmpresaId, ct)
            ? ResultadoEnvio.Enviada
            : ResultadoEnvio.Falhou;
    }

    /// <summary>===================== O ENVIO DA PESQUISA =====================
    ///
    /// Mesmo protocolo do lembrete: grava, SO ENTAO posta. A ancora do dedupe e `uq_msg_nps`.
    ///
    /// ⚠️ NAO HA `ReservarNpsSemPostarAsync`, e a ausencia e deliberada. O reserve-defer do
    /// lembrete existe porque a `data_alvo` dele so vive na linha da mensagem — reservar e o unico
    /// jeito de nao perder o dia. A pesquisa tem `pesquisas_nps.data_agendada`, uma coluna propria
    /// e durável: fora da janela, o motor simplesmente ADIA a data e nao grava mensagem nenhuma.
    ///
    /// Reservar sem postar aqui seria pior: a linha ficaria pendente, a drenagem a postaria num
    /// momento que o motor nao escolheu, e o `data_envio` da pesquisa — de onde sai o relogio da
    /// expiracao — nao teria como acompanhar.
    /// ================================================================</summary>
    /// <summary>===================== A RESERVA BARRADA NAO QUER DIZER "SAIU" =====================
    ///
    /// `uq_msg_nps` barra a segunda linha da mesma venda. A primeira versao tratava "barrou" como
    /// "ja foi enviada" — e nao e: a linha existe tambem quando o POST FALHOU. E nada mais a
    /// reenviava, porque a drenagem do follow-up so pega linha com `lembrete_id`. Resultado: o
    /// cliente nunca era perguntado, a pesquisa era marcada `enviada` no dia seguinte, expirava, e
    /// entrava no relatorio como "nao respondeu".
    ///
    /// Agora a barrada OLHA A LINHA que ja existe:
    ///
    ///   · saiu (`enviada_em` preenchido) -> `Barrada`, com o id e a hora REAIS dela na reserva,
    ///     para a pesquisa ser marcada com a hora em que a pergunta chegou de verdade;
    ///   · nao saiu -> posta A MESMA LINHA de novo, com o texto que ela guardou. Uma linha so por
    ///     venda continua valendo, e a tentativa diaria tem fim: a pesquisa que passa da
    ///     `data_limite` e cancelada pela rodada.
    /// ==========================================================================================</summary>
    public async Task<ResultadoEnvio> EnviarNpsAsync(
        Mensagem reserva, string telefone, CancellationToken ct)
    {
        var id = await dados.ReservarNpsAsync(reserva, ct);

        if (id is not null)
        {
            reserva.Id = id.Value;
            return await DispararAsync(
                    reserva.InstanceName, telefone, reserva.Texto ?? "", id.Value, reserva.EmpresaId, ct)
                ? ResultadoEnvio.Enviada
                : ResultadoEnvio.Falhou;
        }

        var existente = await dados.PerguntaNpsDaVendaAsync(reserva.EmpresaId, reserva.NegociacaoId!.Value, ct);

        // Barrou e nao ha linha: so acontece se ela foi apagada entre o INSERT e esta leitura. Nada
        // a reenviar, e o comportamento antigo vale.
        if (existente == null) return ResultadoEnvio.Barrada;

        reserva.Id = existente.Id;

        if (existente.EnviadaEm != null)
        {
            reserva.EnviadaEm = existente.EnviadaEm;
            return ResultadoEnvio.Barrada;
        }

        return await DispararAsync(
                existente.InstanceName, telefone, existente.Texto ?? "", existente.Id, existente.EmpresaId, ct)
            ? ResultadoEnvio.Enviada
            : ResultadoEnvio.Falhou;
    }

    /// <summary>RESERVA o lembrete SEM postar — usado quando esta fora da janela de atendimento
    /// ou a conexao caiu. A linha fica pendente (enviada_em NULL) para nao perder a data-alvo
    /// exata; a proxima drenagem dentro da janela a posta.
    ///
    /// Sem id de volta = barrada por invariante (este lembrete ja gerou mensagem).</summary>
    public async Task<ResultadoEnvio> ReservarLembreteAsync(Mensagem reserva, CancellationToken ct)
    {
        var id = await dados.ReservarLembreteAsync(reserva, ct);
        if (id is null) return ResultadoEnvio.Barrada;

        reserva.Id = id.Value;
        return ResultadoEnvio.Adiada;
    }

    /// <summary>===================== O AGRADECIMENTO DA PESQUISA =====================
    ///
    /// Grava e posta, como todo o resto. Tres coisas o separam do envio da PERGUNTA:
    ///
    /// ⚠️ 1. NAO HA ANCORA DE DEDUPE. `uq_msg_nps` e unico em `negociacao_id` filtrado por
    ///    `tipo_automacao = 'nps'`, e a pergunta JA OCUPA aquela vaga — preencher `negociacao_id`
    ///    aqui faria o agradecimento ser recusado pelo indice. Quem garante que ele sai uma vez e o
    ///    chamador: a transicao de status da pesquisa e um UPDATE condicional, e a acao so corre
    ///    quando ele afetou UMA linha.
    ///
    /// ⚠️ 2. AS MARCAS VEM DA ENTIDADE, e aqui isso FUNCIONA. Este caminho usa
    ///    `GravarManualAsync`, que faz `db.Add` — diferente do `ReservarLembreteAsync` e do
    ///    `ReservarNpsAsync`, que gravam por SQL cru listando colunas e por isso ignoram
    ///    propriedade nova. A assimetria e uma armadilha conhecida (ver o comentario do
    ///    `ReservarLembreteAsync`), e quem chamar daqui tem de marcar `Origem` e `TipoAutomacao`.
    ///
    /// ⚠️ 3. SEM TETO DIARIO, e e deliberado: e RESPOSTA, nao disparo. Sai segundos depois de o
    ///    cliente escrever, e o freio por contato existe contra automatica NAO SOLICITADA.
    /// ======================================================================</summary>
    public async Task<ResultadoEnvio> EnviarAgradecimentoNpsAsync(
        Mensagem reserva, string telefone, CancellationToken ct)
    {
        reserva.Origem = OrigemMensagem.Automatica;
        reserva.TipoAutomacao = Entidades.TipoAutomacao.Nps;

        var (_, resultado) = await EnviarManualAsync(reserva, telefone, ct);

        return resultado;
    }

    /// <summary>Resposta MANUAL do vendedor. Sem teto diario, sem espacamento, sem reserve-defer
    /// — ele decide quando e quantas vezes. O que continua igual: grava antes de disparar, e o
    /// numero real e resolvido pelo cliente (o nono digito).</summary>
    public async Task<(long MensagemId, ResultadoEnvio Resultado)> EnviarManualAsync(
        Mensagem mensagem, string telefone, CancellationToken ct)
    {
        var id = await dados.GravarManualAsync(mensagem, ct);

        var ok = await DispararAsync(
            mensagem.InstanceName, telefone, mensagem.Texto ?? "", id, mensagem.EmpresaId, ct);

        return (id, ok ? ResultadoEnvio.Enviada : ResultadoEnvio.Falhou);
    }

    /// <summary>Midia MANUAL do vendedor (MID-1). Mesmo protocolo do texto: GRAVA A LINHA, depois
    /// dispara. Invertê-lo produziria mensagem entregue ao cliente sem registro nenhum no
    /// sistema — o unico erro deste desenho que nao tem conserto depois.
    ///
    /// `base64` chega pronto de quem ja validou e guardou o arquivo: o Core nao le disco.</summary>
    public async Task<(long MensagemId, ResultadoEnvio Resultado)> EnviarMidiaManualAsync(
        Mensagem mensagem, string telefone, string base64, string mime, string nomeArquivo,
        string? legenda, CancellationToken ct)
    {
        var id = await dados.GravarManualAsync(mensagem, ct);

        var ok = await DispararAsync(telefone, id, mensagem.EmpresaId,
            Postar(mensagem.InstanceName, telefone, base64, mime, nomeArquivo, legenda), ct);

        return (id, ok ? ResultadoEnvio.Enviada : ResultadoEnvio.Falhou);
    }

    /// <summary>TEMPLATE aprovado, enviado pelo vendedor com a janela de 24h fechada (INT-XX). Mesmo
    /// protocolo: grava a linha — com o texto JA PREENCHIDO, que e o que o cliente vai ler —, depois
    /// dispara pela mesma barreira de todo envio.</summary>
    public async Task<(long MensagemId, ResultadoEnvio Resultado)> EnviarModeloManualAsync(
        Mensagem mensagem, string telefone, ModeloParaEnvio modelo, CancellationToken ct)
    {
        var id = await dados.GravarManualAsync(mensagem, ct);

        var ok = await DispararAsync(telefone, id, mensagem.EmpresaId,
            c => whatsapp.EnviarModeloAsync(mensagem.InstanceName, telefone, modelo, c), ct);

        return (id, ok ? ResultadoEnvio.Enviada : ResultadoEnvio.Falhou);
    }

    /// <summary>Reenvia o TEMPLATE que nao saiu. Mandar o texto da linha como texto livre seria
    /// recusado pela Meta fora da janela — a linha guarda qual template ela era.</summary>
    public async Task<ResultadoEnvio> ReenviarModeloAsync(
        Mensagem pendente, string telefone, ModeloParaEnvio modelo, CancellationToken ct) =>
        await DispararAsync(telefone, pendente.Id, pendente.EmpresaId,
            c => whatsapp.EnviarModeloAsync(pendente.InstanceName, telefone, modelo, c), ct)
            ? ResultadoEnvio.Enviada
            : ResultadoEnvio.Falhou;

    /// <summary>Reenvia MIDIA que ficou pelo caminho. Como o reenvio de texto: MESMA linha, sem
    /// reserva nova — a invariante de dedupe segue valendo e nao ha risco de duplicar.</summary>
    public async Task<ResultadoEnvio> ReenviarMidiaAsync(
        Mensagem pendente, string telefone, string base64, string mime, string nomeArquivo,
        string? legenda, CancellationToken ct) =>
        await DispararAsync(telefone, pendente.Id, pendente.EmpresaId,
            Postar(pendente.InstanceName, telefone, base64, mime, nomeArquivo, legenda), ct)
            ? ResultadoEnvio.Enviada
            : ResultadoEnvio.Falhou;

    /// <summary>===================== AUDIO TEM ROTA PROPRIA =====================
    /// `sendMedia` com `mediatype=audio` entrega o arquivo como ANEXO comum. Nota de voz — com
    /// onda, velocidade e o comportamento que o cliente espera — sai por `sendWhatsAppAudio`.
    ///
    /// Os dois devolvem 2xx. A diferenca so aparece no celular de quem recebe, e foi assim que
    /// o defeito passou: a linha ficava `enviada`, sem erro, e nada chegava.
    ///
    /// A escolha e feita AQUI, num lugar so, para o envio novo e o reenvio nao divergirem.
    /// ==================================================================</summary>
    private Func<CancellationToken, Task<string>> Postar(
        string instancia, string telefone, string base64, string mime, string nomeArquivo,
        string? legenda) =>
        ValidadorMidia.TipoDe(mime) == TipoMidia.Audio
            ? c => whatsapp.EnviarAudioAsync(instancia, telefone, base64, c)
            : c => whatsapp.EnviarMidiaAsync(
                instancia, telefone, base64,
                ValidadorMidia.MediatypeDe(mime), mime, nomeArquivo, legenda, c);

    /// <summary>Reenvia o que ficou pelo caminho. Reaproveita a MESMA linha — nao cria reserva
    /// nova, entao a invariante segue valendo e nao ha risco de duplicar.</summary>
    public async Task<ResultadoEnvio> ReenviarAsync(
        Mensagem pendente, string telefone, CancellationToken ct) =>
        await DispararAsync(
                pendente.InstanceName, telefone, pendente.Texto ?? "", pendente.Id, pendente.EmpresaId, ct)
            ? ResultadoEnvio.Enviada
            : ResultadoEnvio.Falhou;

    /// <summary>As reservas ainda dentro da janela de reenvio.</summary>
    public Task<IReadOnlyList<Mensagem>> PendentesAsync(long empresaId, CancellationToken ct) =>
        dados.PendentesAsync(empresaId, LimiteDaJanela(), ct);

    /// <summary>Marca as reservas que passaram da janela. Chamar ANTES de drenar: o que expirou
    /// nao deve nem ser tentado, e a partir daqui aparece como numero proprio no endpoint de
    /// saude, em vez de sumir do radar como acontece no Recupera.</summary>
    public Task<int> ExpirarVencidasAsync(long empresaId, CancellationToken ct) =>
        dados.ExpirarVencidasAsync(empresaId, LimiteDaJanela(), ct);

    /// <summary>FREIO POR CONEXAO: a instancia esta pareada AGORA?
    ///
    /// Uma checagem por empresa antes de disparar em lote. Com o numero caido, postar so
    /// empilha falha na coluna `erro` e atrasa a fila — melhor reservar sem postar e deixar a
    /// drenagem recuperar quando voltar. Mantem quem chama sem conhecer a Evolution.</summary>
    public async Task<bool> InstanciaConectadaAsync(string instancia, CancellationToken ct) =>
        string.Equals(await whatsapp.StatusInstanciaAsync(instancia, ct), "open",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Pausa entre disparos automaticos. Usa o TimeProvider para o teste nao esperar
    /// 3 segundos de verdade.</summary>
    public Task EspacarAsync(CancellationToken ct) =>
        opcoes.IntervaloEntreEnvios > TimeSpan.Zero
            ? Task.Delay(opcoes.IntervaloEntreEnvios, relogio, ct)
            : Task.CompletedTask;

    private DateOnly LimiteDaJanela() =>
        DateOnly.FromDateTime(relogio.GetUtcNow().UtcDateTime).AddDays(-opcoes.JanelaReenvioDias);

    /// <summary>O texto que fica em `mensagens.erro` quando o disparo é recusado por ser
    /// demonstração. Fica público para o teste afirmar sobre ele em vez de repetir a string.</summary>
    public const string MotivoDemonstracao =
        "Envio bloqueado: esta é uma empresa de DEMONSTRAÇÃO. " +
        "Os contatos são fictícios e nenhuma mensagem sai daqui.";

    private Task<bool> DispararAsync(
        string instancia, string destino, string texto, long mensagemId, long empresaId,
        CancellationToken ct) =>
        DispararAsync(destino, mensagemId, empresaId,
            c => whatsapp.EnviarTextoAsync(instancia, destino, texto, c), ct);

    /// <summary>===================== UMA BARREIRA, DOIS CONTEUDOS (MID-1) =====================
    /// O que muda entre mandar texto e mandar imagem e SO o POST na Evolution. Tudo o mais — a
    /// recusa de demonstracao, o `ConfirmarEnvio`, o `RegistrarFalha`, o log — vale igual.
    ///
    /// Por isso o postar entra como delegate em vez de existir um segundo metodo parecido: um
    /// caminho novo de envio que esquecesse a checagem de demonstracao mandaria mensagem de
    /// verdade para contato ficticio, e ninguem descobriria ate o cliente reclamar.
    /// ==============================================================================</summary>
    private async Task<bool> DispararAsync(
        string destino, long mensagemId, long empresaId,
        Func<CancellationToken, Task<string>> postar, CancellationToken ct)
    {
        // ===================== A ÚLTIMA BARREIRA =====================
        // Todo envio do sistema passa por aqui — lembrete automático, resposta manual e reenvio.
        // É o único ponto onde uma checagem só não pode ser esquecida por um caminho novo.
        //
        // As duas checagens são independentes DE PROPÓSITO, e cobrem falhas diferentes:
        //
        //   • a EMPRESA marcada como demonstração cobre o caso de alguém trocar o telefone de um
        //     contato semeado por um número real;
        //   • o NÚMERO na faixa reservada cobre o caso inverso — contato de demonstração que
        //     acabou num tenant comum (importação, cópia de base, engano).
        //
        // Nenhuma delas depende da outra estar certa.
        var ehDemonstracao = TelefoneDemonstracao.EhDemonstracao(destino)
                          || await dados.EhDemonstracaoAsync(empresaId, ct);

        if (ehDemonstracao)
        {
            log.LogWarning(
                "Envio RECUSADO para {Destino} (mensagem {Id}, empresa {Empresa}): demonstração.",
                destino, mensagemId, empresaId);

            // Registra a falha em vez de apagar a linha: é o mesmo protocolo de qualquer outra
            // falha de entrega, e a tela já sabe mostrar "não entregue" com o motivo. Apagar
            // liberaria a invariante de dedupe.
            await dados.RegistrarFalhaAsync(mensagemId, MotivoDemonstracao, ct);
            return false;
        }

        try
        {
            var waMessageId = await postar(ct);
            await dados.ConfirmarEnvioAsync(mensagemId, waMessageId, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A linha FICA, com o erro gravado e o contador de tentativas incrementado.
            log.LogError(ex, "Falha ao enviar a mensagem {Id} para {Destino}.", mensagemId, destino);
            await dados.RegistrarFalhaAsync(mensagemId, ex.Message, ct);
            return false;
        }
    }
}
