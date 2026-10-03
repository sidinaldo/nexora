using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Conversoes;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Conversoes;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>A credencial de anúncio da empresa, na área logada (INT-4).
///
/// Query filter global vale — é caminho autenticado. O oposto do motor de envio, que roda como job.
/// O enforcement de PAPEL é do controller, como no resto do sistema.
///
/// ===================== UMA PLATAFORMA HOJE, DUAS AMANHÃ =====================
/// Todo método filtra por `PlataformaConversao.Meta` explicitamente, em vez de pegar "a credencial
/// da empresa". A tabela já tem chave `(empresa_id, plataforma)`, e o dia em que o Google entrar,
/// um `FirstOrDefault` sem plataforma passaria a devolver a credencial errada — em silêncio, e no
/// caminho que manda dado pessoal para fora.
/// ============================================================================</summary>
public class ServicoConversoes(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    IClienteMeta cliente,
    TimeProvider relogio,
    // INT-5: por o evento na fila e trabalho DELE. Uma segunda rota de enfileiramento aqui seria
    // o unico erro grave disponivel neste bloco — o `ON CONFLICT`, o payload e o `ganha_em` real
    // moram todos la, com 910 linhas de teste em cima.
    IPublicadorConversoes publicador) : IServicoConversoes
{
    /// <summary>A janela do número que cobra quem não conectou. Trinta dias porque é o recorte que
    /// o resto do painel usa, e porque mais do que isso incluiria lead que a Meta já não aceita
    /// (a janela dela é de 7 dias).</summary>
    private const int DiasDoAviso = 30;

    /// <summary>Hoje só a Meta. A constante existe para que a próxima plataforma seja um parâmetro,
    /// e não uma caçada por `FirstOrDefault` sem filtro.</summary>
    private const PlataformaConversao Plataforma = PlataformaConversao.Meta;

    public async Task<PainelConversoes> ObterAsync(CancellationToken ct)
    {
        // ⚠️ A ENTIDADE, E NÃO UMA PROJEÇÃO (INT-5). `PodeEnviar` e `MotivosParados` são métodos
        // DELA, e a regra pergunta pelo token de verdade — não pelo mascarado que sai no DTO. Com
        // a projeção anônima de antes, o painel precisava reescrever o portão por fora, e foi
        // exatamente assim que o `Enviando` acabou errado: ele esquecia `EmLead`/`EmCompra`.
        //
        // É uma linha só, e a projeção já trazia todas as colunas. O `Include` troca uma segunda
        // consulta pelo nome de quem declarou o consentimento.
        var linha = await db.CredenciaisConversao.AsNoTracking()
            .Include(c => c.ConsentimentoUsuario)
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        var credencial = linha is null ? null : new CredencialDto(
            linha.Id,
            linha.Plataforma.ToString().ToLowerInvariant(),
            linha.Identificador,
            // SUFIXO, NUNCA O TOKEN. Quatro caracteres bastam para a pessoa reconhecer QUAL token
            // está lá, e não servem para chamar a Graph API com ele.
            // ⚠️ A MÁSCARA É FEITA EM MEMÓRIA: `Mascarar` é método nosso, e dentro de um `Select`
            // o EF não sabe traduzi-lo — estoura em runtime, quando alguém abre a tela.
            linha.Token is null ? null : Mascarar(linha.Token),
            linha.CodigoTeste,
            linha.PaginaId,
            linha.Ativo,
            linha.EmLead,
            linha.EmCompra,
            linha.ConsentimentoEm,
            linha.ConsentimentoUsuario?.Nome,
            linha.DesativadaEm,
            linha.DesativadaMotivo,
            linha.CriadoEm,
            linha.PodeEnviar(TipoConversao.Lead) || linha.PodeEnviar(TipoConversao.Compra),
            linha.MotivosParados(TipoConversao.Compra));

        return new PainelConversoes(
            credencial, await LeadsComAnuncioAsync(ct), await ConversoesAsync(ct),
            // Sem credencial nenhuma a lista não tem sentido: a tela tem outro trabalho antes,
            // que é conectar. Mostrar "6 vendas não enviadas" para quem nunca conectou é cobrar
            // de alguém uma coisa que ele ainda não escolheu.
            linha is null
                ? new VendasSemEnvio(0, 0m, PoliticaConversao.DiasDaListaDeNaoEnviadas, [])
                : await VendasSemEnvioAsync(linha.CriadoEm, ct));
    }

    public async Task<ResumoConversoes> ResumoAsync(CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        // ⚠️ `PodeEnviar`, e não `Ativo`. A pergunta do aviso é "está saindo?", e a resposta mora na
        // entidade — uma segunda versão dela aqui divergiria no dia em que o portão mudasse.
        var enviando = credencial?.PodeEnviar(TipoConversao.Lead) == true
                    || credencial?.PodeEnviar(TipoConversao.Compra) == true;

        return new ResumoConversoes(enviando, await LeadsComAnuncioAsync(ct));
    }

    /// <summary>Quantos leads dos últimos 30 dias chegaram com identificador de clique.
    ///
    /// ===================== POR QUE ESTE NÚMERO E NÃO "leads do site" =====================
    /// `identificadores <> '{}'` é a pergunta certa: são as pessoas que vieram de um ANÚNCIO pago e
    /// para quem existe o que mandar de volta. Contar todo lead do formulário inflaria o número com
    /// quem chegou pelo Google orgânico, e a frase "a Meta não ficou sabendo de 40" seria falsa —
    /// a Meta não tem nada a saber sobre 28 deles.
    /// ==================================================================================</summary>
    private Task<int> LeadsComAnuncioAsync(CancellationToken ct)
    {
        var desde = relogio.GetUtcNow().UtcDateTime.AddDays(-DiasDoAviso);

        return db.RastreiosLead.AsNoTracking()
            .Where(r => r.OcorridoEm >= desde && r.Identificadores != "{}")
            .CountAsync(ct);
    }

    public async Task SalvarAsync(SalvarCredencial dados, CancellationToken ct)
    {
        var identificador = (dados.Identificador ?? "").Trim();
        if (identificador.Length == 0)
            throw new RegraDeNegocioException("Informe o ID do pixel.");

        // Só dígitos: o Pixel ID da Meta é numérico, e o erro comum é colar a URL do Gerenciador
        // de Eventos inteira. Recusar aqui é um erro na tela; aceitar é um 400 da Graph API três
        // dias depois, quando a primeira venda fechar.
        if (!identificador.All(char.IsAsciiDigit))
            throw new RegraDeNegocioException(
                "O ID do pixel é só números — copie o campo \"ID do conjunto de dados\" no "
                + "Gerenciador de Eventos.");

        // ⚠️ TODA VALIDAÇÃO ANTES DE TOCAR NO ChangeTracker, e um teste ensinou por quê: a checagem
        // da página estava depois do `Add`, então a recusa deixava uma credencial nova RASTREADA e
        // não salva — e o `SalvarAsync` seguinte, no mesmo escopo, tentava inserir duas e violava
        // `uq_credenciais_empresa_plataforma`.
        //
        // Em produção cada requisição tem o próprio escopo e isso não apareceria. Validar antes de
        // mutar não depende de sorte de escopo.
        //
        // Só dígitos, pela mesma razão do Pixel ID: o erro comum é colar o endereço da página.
        var pagina = Vazio(dados.PaginaId);
        if (pagina is not null && !pagina.All(char.IsAsciiDigit))
            throw new RegraDeNegocioException(
                "O ID da página é só números — copie o número da página, não o endereço dela.");

        var credencial = await db.CredenciaisConversao
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial is null)
        {
            credencial = new CredencialConversao
            {
                EmpresaId = contexto.EmpresaId,
                Plataforma = Plataforma
            };
            db.CredenciaisConversao.Add(credencial);
        }

        credencial.Identificador = identificador;
        credencial.CodigoTeste = Vazio(dados.CodigoTeste);

        credencial.PaginaId = pagina;
        credencial.Ativo = dados.Ativo;
        credencial.EmLead = dados.EmLead;
        credencial.EmCompra = dados.EmCompra;

        // ⚠️ TOKEN EM BRANCO MANTÉM O ANTERIOR. A tela não tem como preenchê-lo de volta — o `GET`
        // nunca o devolve —, então um PUT com o campo vazio é sempre "não mexi nisso". Apagar aqui
        // faria de "trocar o Pixel ID" um jeito de desligar o envio por descuido.
        var token = Vazio(dados.Token);
        if (token is not null) credencial.Token = token;

        // ===================== O CONSENTIMENTO: DATA E AUTOR, NÃO UM BOOL =====================
        // Só escreve quando MUDA, senão cada salvamento de qualquer outro campo reescreveria a data
        // e o registro passaria a dizer que a declaração é de hoje.
        // ====================================================================================
        var agora = relogio.GetUtcNow().UtcDateTime;
        var tinha = credencial.ConsentimentoEm is not null;

        if (dados.ConsentimentoDeclarado && !tinha)
        {
            credencial.ConsentimentoEm = agora;
            credencial.ConsentimentoPor = contexto.UsuarioId;
        }
        else if (!dados.ConsentimentoDeclarado && tinha)
        {
            credencial.ConsentimentoEm = null;
            credencial.ConsentimentoPor = null;
        }

        // Religar a mão limpa a desativação do MOTOR. É o gesto de "troquei o token, tenta de
        // novo": sem isto, a credencial ficaria desativada para sempre depois do primeiro token
        // recusado, e a pessoa não teria como dizer ao sistema que resolveu.
        if (credencial.Ativo && token is not null)
        {
            credencial.DesativadaEm = null;
            credencial.DesativadaMotivo = null;
        }

        await db.SaveChangesAsync(ct);

        // Retirar o consentimento, ou desligar, esvazia a fila. Ver `CancelarPendentesAsync`.
        if (!dados.ConsentimentoDeclarado || !dados.Ativo)
            await CancelarPendentesAsync("o consentimento foi retirado", ct);
    }

    public async Task RemoverAsync(CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial is null) return;

        db.CredenciaisConversao.Remove(credencial);
        await db.SaveChangesAsync(ct);

        await CancelarPendentesAsync("a credencial foi removida", ct);
    }

    /// <summary>===================== FILA QUE NÃO PODE DRENAR NÃO FICA NA FILA =====================
    ///
    /// O `payload` de cada conversão pendente guarda SHA-256 de telefone e e-mail. Sem token, sem
    /// consentimento ou com o envio desligado, nenhuma delas vai sair nunca — e o que sobraria é
    /// dado pessoal hasheado parado numa tabela, esperando o dia em que alguém religue e ele saia
    /// sem que ninguém tenha decidido isso agora.
    ///
    /// `cancelado` e não `DELETE`: o evento fica registrado, o dado sai. A mesma regra da trilha de
    /// auditoria — a tela precisa poder dizer "isto não foi enviado, e por quê".
    ///
    /// `ExecuteUpdateAsync` num comando só: pode haver centenas de linhas, e carregá-las para
    /// mudar dois campos seria trabalho para nada.
    /// ====================================================================================</summary>
    private Task CancelarPendentesAsync(string motivo, CancellationToken ct) =>
        db.EventosConversao
            .Where(e => e.Plataforma == Plataforma && e.Status == StatusConversao.Pendente)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, StatusConversao.Cancelado)
                .SetProperty(e => e.ProximaTentativaEm, (DateTime?)null)
                .SetProperty(e => e.Erro, motivo), ct);

    /// <summary>===================== AS VENDAS QUE A META NUNCA VIU (INT-5) =====================
    ///
    /// Vendas fechadas que não têm evento de conversão nenhum. DERIVADA, porque não há o que ler:
    /// quando o portão está fechado, `PublicadorConversoes` devolve `void` sem gravar linha, sem
    /// log e sem contador. O estrago não deixa rastro — só a ausência dele.
    ///
    /// ===================== O TETO DE 30 DIAS NÃO É ESCOLHA =====================
    /// `MotorConversoes.ExpurgarAntigosAsync` apaga evento com mais de `DiasDeRetencao` dias. Então
    /// além dessa janela um `NOT EXISTS` NÃO DISTINGUE "nunca foi enfileirada" de "foi enfileirada,
    /// entregue, e a linha foi expurgada" — e a tela acusaria de perdida uma venda que chegou.
    ///
    /// ⚠️ A CONSTANTE É A MESMA DO EXPURGO, de propósito. Se alguém diminuir a retenção e este
    /// número ficar para trás, a lista passa a MENTIR. Ligados, o pior que acontece é a lista ficar
    /// mais curta do que podia.
    /// ==========================================================================
    ///
    /// Os predicados são os do `ix_negociacoes_ganhas` — `(empresa_id, ganha_em) WHERE ganha_em IS
    /// NOT NULL AND status &lt;&gt; 'cancelada'`. Não é coincidência arranjada: venda cancelada não entra
    /// porque ela não aconteceu, e escrever a regra certa é o que faz a consulta cair no índice.
    ///
    /// Contato anonimizado fica de fora: o titular pediu para sumir, e o evento iria sem telefone,
    /// sem e-mail e sem `fbc` — sem nada com que a Meta pudesse casar.
    ///
    /// E qualquer evento serve para excluir, em QUALQUER estado. O que falhou é do botão "Reenviar"
    /// que já existe na tabela de baixo; duas telas para o mesmo gesto confundem mais do que a
    /// omissão.</summary>
    private async Task<VendasSemEnvio> VendasSemEnvioAsync(
        DateTime credencialCriadaEm, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        // ===================== DOIS PISOS, E O SEGUNDO É O QUE FAZ O DIA UM NÃO SER UM MURO =====================
        // O da janela vem da política. O outro é a data em que a empresa CONECTOU o pixel: antes
        // dela não havia para onde mandar, e listar três semanas de vendas antigas como "não
        // enviadas" no dia em que alguém conecta é receber a pessoa com vinte linhas de acusação
        // na única tela que precisa ganhar a confiança dela.
        //
        // O rodapé da tela já promete isso em palavras: "o que não dá para recuperar é o histórico
        // de antes de você conectar".
        //
        // ⚠️ `CriadoEm` DA CREDENCIAL, E NUNCA `ConsentimentoEm`. Ancorar no consentimento
        // esconderia exatamente o caso que originou este bloco — o consentimento chegou depois, e
        // as vendas perdidas são as de ANTES dele.
        // ==================================================================================
        var desde = Maior(
            agora.AddDays(-PoliticaConversao.DiasDaListaDeNaoEnviadas), credencialCriadaEm);

        var limiteDoPrazo = agora.AddDays(-PoliticaConversao.DiasDeValidade);

        var consulta = db.Negociacoes.AsNoTracking()
            // `GanhaEm != null` e `status <> cancelada` são os predicados do `ix_negociacoes_ganhas`.
            // Não é coincidência arranjada: venda cancelada não entra porque o dinheiro foi
            // desfeito — `ServicoVendas.CancelarAsync` MANTÉM o `ganha_em` de propósito, então sem
            // esta cláusula um estorno viraria "conversão perdida" com botão para reenviá-la.
            .Where(n => n.GanhaEm != null && n.Status != StatusNegociacao.Cancelada)
            // E o par explícito, para a correção não depender de o Postgres provar que um OU de
            // igualdades implica a desigualdade acima.
            .Where(n => n.Status == StatusNegociacao.Ganha
                     || n.Status == StatusNegociacao.Concluida)
            .Where(n => n.GanhaEm >= desde)
            .Where(n => n.Contato.AnonimizadoEm == null)
            // `Tipo == Compra` é redundante pelo `ck_conversoes_negociacao` (linha de lead tem
            // `negociacao_id` nulo), e entra mesmo assim: é o predicado do índice parcial
            // `uq_conversoes_compra`, e é o que faz a anti-junção ser uma sonda por candidato.
            .Where(n => !db.EventosConversao.Any(
                e => e.NegociacaoId == n.Id && e.Tipo == TipoConversao.Compra));

        // ⚠️ OS TOTAIS SAEM DE CONSULTA PRÓPRIA, E NÃO DA PÁGINA. Com o teto de 50, somar o que
        // veio diria um número menor que a verdade — e o número é o ponto da tela: "6 vendas,
        // R$ 1.527,85, não chegaram na Meta" é o que faz alguém agir; "6 pendências" não é.
        var total = await consulta.CountAsync(ct);
        var valorTotal = await consulta.SumAsync(n => n.Valor ?? 0m, ct);

        var vendas = await consulta
            // ⚠️ QUEM AINDA DÁ TEMPO VEM PRIMEIRO, e isto é o teto não poder esconder um botão:
            // uma empresa com 60 vendas vencidas e 3 dentro do prazo encheria as 50 vagas com
            // linhas sem botão, e as três que dava para salvar ficariam invisíveis.
            .OrderByDescending(n => n.GanhaEm > limiteDoPrazo)
            // Dentro de cada grupo, a mais apertada primeiro: é a que vence antes.
            .ThenBy(n => n.GanhaEm)
            .Take(UltimasConversoes)
            .Select(n => new { n.Id, Contato = n.Contato.Nome, n.Valor, n.GanhaEm })
            .ToListAsync(ct);

        var linhas = vendas.Select(v =>
        {
            // A MESMA conta do publicador: `ocorrido_em + DiasDeValidade`. Aqui é previsão — a
            // linha ainda não existe —, e é o que a tela usa para dizer quanto falta.
            var expira = v.GanhaEm!.Value.AddDays(PoliticaConversao.DiasDeValidade);

            return new VendaSemConversaoDto(
                v.Id, v.Contato, v.Valor, v.GanhaEm.Value, expira, ForaDoPrazo: expira <= agora);
        });

        return new VendasSemEnvio(
            total, valorTotal, PoliticaConversao.DiasDaListaDeNaoEnviadas, [.. linhas]);
    }

    private static DateTime Maior(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>As últimas conversões da empresa.
    ///
    /// Cinquenta, como o registro de webhooks: responde "está chegando?" e "o que falhou hoje?";
    /// mais que isso é trabalho para uma consulta, não para uma tela.</summary>
    private const int UltimasConversoes = 50;

    private Task<List<ConversaoDto>> ConversoesAsync(CancellationToken ct) =>
        db.EventosConversao.AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(UltimasConversoes)
            .Select(e => new ConversaoDto(
                e.Id,
                e.Tipo.ToString().ToLowerInvariant(),
                e.Status.ToString().ToLowerInvariant(),
                e.Contato.Nome,
                e.Negociacao == null ? null : e.Negociacao.Valor,
                e.Tentativas,
                e.CodigoResposta,
                e.CodigoMeta,
                e.FbtraceId,
                e.Erro,
                e.OcorridoEm,
                e.ExpiraEm,
                e.EntregueEm,
                e.CriadoEm,
                e.Payload,
                // ⚠️ SÓ O QUE DESISTIU. `pendente` já vai ser tentado sozinho; `entregue` mandaria o
                // mesmo evento duas vezes; e `expirado` NUNCA vai funcionar — oferecer o botão ali
                // seria oferecer um gesto que só pode fracassar.
                e.Status == StatusConversao.Falhou))
            .ToListAsync(ct);

    /// <summary>===================== PÔR NA FILA UMA VENDA QUE FICOU PARA TRÁS (INT-5) =====================
    ///
    /// Reusa `PublicarCompraAsync` inteiro: ele relê a venda, o contato e o rastro, monta o payload
    /// com o `ganha_em` REAL e insere com `ON CONFLICT (negociacao_id) DO NOTHING`. Clicar duas
    /// vezes não duplica nada — a idempotência vem do índice, não de um guarda nosso.
    ///
    /// ===================== ⚠️ MAS O PUBLICADOR NÃO FOI FEITO PARA RECEBER ID DE CLIENTE =====================
    /// Ele usa `IgnoreQueryFilters()` e tira a empresa DA PRÓPRIA LINHA (`PublicadorConversoes:96`),
    /// porque os três chamadores de hoje vêm de dentro de uma transação e dois rodam sem tenant de
    /// propósito. No instante em que um USUÁRIO escolhe o id, isso vira vazamento entre empresas
    /// COM EFEITO EXTERNO: o dono da empresa A manda o id de uma venda da B, o publicador carrega a
    /// venda da B, busca a credencial DA B e dispara um Purchase no pixel DA B.
    ///
    /// Nada falha, nada loga como estranho, e a vítima vê um evento que ela queria ter. Quase
    /// invisível.
    ///
    /// Por isso a venda é resolvida AQUI, por `db.Negociacoes` COM o filtro de tenant. O publicador
    /// só vê um id que já provou ser desta empresa.
    /// ==============================================================================
    ///
    /// Os outros portões, na ordem em que a pessoa os encontraria:</summary>
    public async Task EnviarVendaAsync(long negociacaoId, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;

        // 1 — É DESTA EMPRESA? O filtro de tenant responde; "não é minha" e "não existe" caem na
        // mesma frase, que é exatamente o que um tenant pode saber do outro.
        var venda = await db.Negociacoes.AsNoTracking()
            .Where(n => n.Id == negociacaoId)
            .Select(n => new { n.Id, n.Status, n.GanhaEm, Anonimizado = n.Contato.AnonimizadoEm })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Venda não encontrada.");

        // 2 — É VENDA? Sem `ganha_em` o publicador usaria `agora` como hora do evento
        // (`venda.GanhaEm ?? agora`) — inventando um `event_time` para algo que não aconteceu, que
        // é precisamente o que o INT-4 recusou fazer na importação.
        if (venda.GanhaEm is not { } fechadaEm
            || venda.Status is not (StatusNegociacao.Ganha or StatusNegociacao.Concluida))
            throw new RegraDeNegocioException("Este negócio ainda não virou venda.");

        if (venda.Anonimizado is not null)
            throw new RegraDeNegocioException(
                "Este contato foi anonimizado. Mandar um evento novo sobre ele desfaria o pedido "
              + "de exclusão que ele fez.");

        // 3 — AINDA DÁ TEMPO? Mesma recusa do `ReenviarAsync`, e pelo mesmo motivo: a tela não
        // oferece gesto que só pode fracassar.
        if (fechadaEm.AddDays(PoliticaConversao.DiasDeValidade) <= agora)
            throw new RegraDeNegocioException(
                $"Esta venda passou dos {PoliticaConversao.DiasDeValidade} dias que a Meta aceita. "
              + "Enviar não vai funcionar.", conflito: true);

        // 4 — O ENVIO ESTÁ LIGADO? ⚠️ SEM ISTO O PUBLICADOR ENGOLE O CLIQUE EM SILÊNCIO e a tela
        // diz "enviado" — o defeito original reaparecendo dentro do próprio conserto. E a recusa
        // reaproveita a PRIMEIRA frase do diagnóstico: o aviso e o erro passam a ser o mesmo texto.
        var credencial = await db.CredenciaisConversao.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial?.PodeEnviar(TipoConversao.Compra) != true)
            throw new RegraDeNegocioException(
                credencial?.MotivosParados(TipoConversao.Compra).FirstOrDefault()
                ?? "Conecte o pixel antes de enviar.", conflito: true);

        await publicador.PublicarCompraAsync(venda.Id, ct);

        // ⚠️ E CONFERE SE ENTROU. `PublicarCompraAsync` devolve `void` e engole toda exceção
        // (é assim de propósito: fechar venda não pode falhar por causa de integração). Sem esta
        // leitura, um INSERT que estourou vira toast de sucesso, a linha continua na lista, e o
        // dono clica para sempre sem explicação — o mesmo silêncio que este bloco existe para
        // acabar. Uma sonda no índice único, num clique de botão.
        if (!await db.EventosConversao.AnyAsync(
                e => e.NegociacaoId == venda.Id && e.Tipo == TipoConversao.Compra, ct))
            throw new RegraDeNegocioException(
                "Não foi possível pôr esta venda na fila. Tente de novo.");
    }

    /// <summary>Todas as que ainda cabem nos 7 dias, até o teto de UMA rodada do motor.
    ///
    /// ⚠️ O TETO É `MaximoPorRodada`, E NÃO UM NÚMERO QUALQUER: é o que o motor drena por rodada.
    /// Com ele, a tela pode prometer "saem em até um minuto" e estar certa. Sem ele, um lote de
    /// duzentas levaria quatro minutos e a frase viraria mentira.</summary>
    public async Task<ResultadoEnvioEmLote> EnviarVendasPendentesAsync(CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial?.PodeEnviar(TipoConversao.Compra) != true)
            throw new RegraDeNegocioException(
                credencial?.MotivosParados(TipoConversao.Compra).FirstOrDefault()
                ?? "Conecte o pixel antes de enviar.", conflito: true);

        var lista = await VendasSemEnvioAsync(credencial.CriadoEm, ct);

        // Só as que ainda dão tempo. A lista traz as vencidas também — elas aparecem marcadas na
        // tela —, e enfileirá-las aqui criaria linha que nasce expirada.
        var podem = lista.Vendas.Where(v => !v.ForaDoPrazo).ToList();
        var agora = podem.Take(PoliticaConversao.MaximoPorRodada).ToList();

        foreach (var venda in agora)
            await publicador.PublicarCompraAsync(venda.NegociacaoId, ct);

        return new ResultadoEnvioEmLote(agora.Count, podem.Count - agora.Count);
    }

    public async Task<ResultadoTesteConversao> TestarAsync(CancellationToken ct)
    {
        var credencial = await db.CredenciaisConversao
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial is null || string.IsNullOrWhiteSpace(credencial.Token))
            throw new RegraDeNegocioException("Configure o pixel e o token antes de testar.");

        // ===================== O TESTE NÃO PASSA PELO PORTÃO DE CONSENTIMENTO =====================
        // E não deveria: `PodeEnviar` protege o dado de uma PESSOA de sair sem base legal. Aqui não
        // há pessoa — o evento é sintético, com um telefone que não existe.
        //
        // Exigir o consentimento para testar faria a ordem de configuração ser "declare que tem
        // autorização, depois descubra se o token funciona", que é a ordem errada.
        // =======================================================================================
        var agora = relogio.GetUtcNow().UtcDateTime;

        var corpo = MontadorEventoMeta.Montar(new FatoDeConversao(
            TipoConversao.Lead,
            Guid.NewGuid(),
            agora,
            // ⚠️ DADO SINTÉTICO, e de propósito. Mandar um contato real faria o botão de teste
            // enviar uma conversão de verdade para o pixel do cliente — e o `Lead` daquela pessoa
            // sairia duas vezes no dia em que ela virasse lead mesmo.
            Email: "teste@nexora.app",
            Telefone: "5500000000000"));

        var resultado = await cliente.EnviarAsync(
            credencial.Identificador, credencial.Token, corpo, credencial.CodigoTeste, ct);

        // ⚠️ NADA É GRAVADO NA FILA. O evento de teste não é conversão de ninguém: uma linha dele no
        // registro ocuparia o único parcial de um contato que nem existe.
        //
        // Mas a desativação da credencial VALE, e é o ponto do botão: se a Meta recusou o token
        // agora, é isto que o dono precisa ver na tela — em vez de descobrir semanas depois que
        // nada saiu.
        var decisao = // ⚠️ COM O SUBCÓDIGO TAMBÉM. Era o único lugar que classificava com menos informação que o
        // motor — e hoje isso é acidentalmente inofensivo, porque o evento de teste sai sempre
        // como `chat` e os subcódigos de anúncio não aparecem. No dia em que um subcódigo passar
        // a afetar a desativação, o motor agiria e o botão "Testar" não: o dono veria "conexão
        // ok" para uma credencial que o motor já desistiu de usar.
        PoliticaConversao.Classificar(resultado.CodigoMeta, resultado.SubcodigoMeta);

        if (decisao.DesativarCredencial && credencial.DesativadaEm is null)
        {
            credencial.DesativadaEm = agora;
            credencial.DesativadaMotivo = decisao.Motivo;
            await db.SaveChangesAsync(ct);
        }
        else if (resultado.Aceitou && credencial.DesativadaEm is not null)
        {
            // Funcionou: o que estava desativado pelo motor volta. É o par do gesto acima — testar
            // depois de trocar o token é como se diz ao sistema que resolveu.
            credencial.DesativadaEm = null;
            credencial.DesativadaMotivo = null;
            await db.SaveChangesAsync(ct);
        }

        return new ResultadoTesteConversao(
            resultado.Aceitou, resultado.Codigo, resultado.FbtraceId, resultado.Erro);
    }

    public async Task ReenviarAsync(long id, CancellationToken ct)
    {
        var evento = await db.EventosConversao.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new RegraDeNegocioException("Conversão não encontrada.");

        if (evento.Status != StatusConversao.Falhou)
            throw new RegraDeNegocioException(
                "Só é possível reenviar uma conversão que falhou.", conflito: true);

        // ⚠️ A JANELA É CONFERIDA AQUI TAMBÉM. Entre a falha e o clique no botão podem passar dias,
        // e um reenvio fora dos 7 dias seria uma requisição que a Meta recusa inteira — com o
        // agravante de o dono ficar achando que resolveu.
        if (evento.ExpiraEm <= relogio.GetUtcNow().UtcDateTime)
            throw new RegraDeNegocioException(
                "Este evento passou dos 7 dias que a Meta aceita. Reenviar não vai funcionar.",
                conflito: true);

        // ⚠️ E A CREDENCIAL PRECISA PODER ENVIAR. Descoberto por um teste: depois de um `190`, o
        // motor desativa a credencial — e reenviar ali devolveria a linha para a fila só para ela
        // falhar de novo na rodada seguinte, com outra mensagem.
        //
        // Mesma regra do `expirado`: a tela não oferece gesto que só pode fracassar. A frase diz o
        // que fazer PRIMEIRO.
        var credencial = await db.CredenciaisConversao.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Plataforma == Plataforma, ct);

        if (credencial?.PodeEnviar(evento.Tipo) != true)
            throw new RegraDeNegocioException(
                "O envio está desligado. Confira o token e o consentimento acima antes de reenviar.",
                conflito: true);

        evento.Status = StatusConversao.Pendente;
        evento.Tentativas = 0;
        evento.ProximaTentativaEm = relogio.GetUtcNow().UtcDateTime;
        evento.Erro = null;
        evento.CodigoResposta = null;
        evento.CodigoMeta = null;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Campo de texto vazio é ausência. Sem isto, salvar com o campo em branco gravaria
    /// string vazia — e `""` é um token, tecnicamente, que só falha na hora de enviar.</summary>
    private static string? Vazio(string? texto)
    {
        var limpo = (texto ?? "").Trim();
        return limpo.Length == 0 ? null : limpo;
    }

    /// <summary>`EAAG…4Zc`. Token curto demais vira só bolinhas: mostrar o começo de um token de 12
    /// caracteres é mostrar um terço dele.</summary>
    private static string Mascarar(string token) =>
        token.Length <= 12 ? "••••••••" : $"{token[..4]}…{token[^4..]}";
}
