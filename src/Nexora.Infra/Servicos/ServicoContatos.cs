using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Webhooks;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O ciclo de vida do contato: cadastro, edição, estado terminal e anonimização.
///
/// PRIMEIRA CAMADA DO PROJETO SEM NADA PARA REAPROVEITAR. O `Devedor` do Recupera é criado por
/// importação de carteira, não por um vendedor digitando; e lá não existe funil, valor de
/// negócio nem "ganhou/perdeu". A única coisa que atravessa é o PADRÃO de anonimização
/// (`Devedor.AnonimizadoEm`), e mesmo esse precisou de solução própria para o telefone.
///
/// Roda autenticado: o query filter global vale, e não há IgnoreQueryFilters aqui.</summary>
public class ServicoContatos(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    IPublicadorEventos eventos,
    ColetorAuditoria trilha,
    TimeProvider relogio) : IServicoContatos
{
    /// <summary>Nome do contato depois de anonimizado. Coluna NOT NULL — não dá para esvaziar.</summary>
    private const string NomeAnonimo = "Contato anonimizado";

    // ==================================================================== leitura
    public async Task<Pagina<ContatoResumo>> ListarAsync(
        FiltroContato filtro, string? busca, long? etapaId, long? responsavelId,
        int pagina, int tamanho, CancellationToken ct)
    {
        pagina = Math.Max(pagina, 1);
        tamanho = Math.Clamp(tamanho, 1, 100);

        // Anonimizado NUNCA aparece em lista nem em busca. Ele existe só para o histórico e para
        // as agregações do dashboard.
        var q = db.Contatos.AsNoTracking().Where(c => c.AnonimizadoEm == null);

        // ===================== O RECORTE VEM DA NEGOCIACAO (E4e) =====================
        // Era `ganho_em`/`perdido_em` no contato. As tres faixas continuam DISJUNTAS, e e por
        // isso que as duas ultimas excluem o que veio antes:
        //
        //   aberto   tem negocio em andamento — e a lista de trabalho do vendedor
        //   ganho    nao tem nenhum aberto, e tem ao menos um fechado
        //   perdido  nao tem aberto nem fechado, e tem ao menos um perdido
        //
        // ⚠️ Sem essa exclusao as faixas se sobrepoem, e quem ganhou e voltou a negociar
        // apareceria nas duas. Hoje isso nao acontece porque reabrir LIMPA o carimbo; com
        // negociacoes as duas linhas coexistem, e a ordem tem de ser dita.
        // ==========================================================================
        q = filtro switch
        {
            FiltroContato.Ganhos => q.Where(c =>
                !c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta)
                && c.Negociacoes.Any(n => n.Status == StatusNegociacao.Ganha
                                       || n.Status == StatusNegociacao.Concluida)),

            FiltroContato.Perdidos => q.Where(c =>
                !c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta
                                     || n.Status == StatusNegociacao.Ganha
                                     || n.Status == StatusNegociacao.Concluida)
                && c.Negociacoes.Any(n => n.Status == StatusNegociacao.Perdida)),

            FiltroContato.Todos => q,

            _ => q.Where(c => c.Negociacoes.Any(n => n.Status == StatusNegociacao.Aberta))
        };

        // A etapa tambem: o contato "esta" na etapa do negocio dele.
        if (etapaId is { } e) q = q.Where(c => c.Negociacoes.Any(n => n.EtapaId == e));
        if (responsavelId is { } r) q = q.Where(c => c.ResponsavelId == r);
        q = AplicarBusca(q, busca);

        // COUNT e página saem os DOIS do SQL. O ServicoInbox do Recupera materializa tudo antes
        // de cortar; aqui o banco conta e o banco corta.
        var total = await q.CountAsync(ct);

        var linhas = await q
            .OrderBy(c => c.Nome).ThenBy(c => c.Id)
            .Skip((pagina - 1) * tamanho)
            .Take(tamanho)
            .Select(c => new
            {
                c.Id, c.Nome, c.Telefone, c.Email, c.Origem,

                // ===================== A POSICAO E O DINHEIRO SAO DO NEGOCIO (E4e) =========
                // O contato nao tem mais etapa nem valor: tem negocios, e cada um deles tem os
                // seus. A lista mostra o VIGENTE — o aberto, ou o mais recente quando nao ha
                // nenhum aberto, que e como o contato ja fechado aparece.
                //
                // ⚠️ Ordena por STATUS, e nao por id: a migracao do elo reinseriu as linhas
                // vindas de venda, e os ids delas ficaram maiores que os das abertas. Id deixou
                // de ser relogio, e isso ja derrubou `RegrasNegociacao` uma vez.
                // ========================================================================
                EtapaId = c.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.EtapaId).FirstOrDefault(),
                EtapaNome = c.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.Etapa.Nome).FirstOrDefault(),
                OrdemKanban = c.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.OrdemKanban).FirstOrDefault(),
                Valor = c.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.Valor).FirstOrDefault(),

                c.ResponsavelId, ResponsavelNome = c.Responsavel == null ? null : c.Responsavel.Nome,

                // "Ja ganhou alguma vez" e "ja perdeu alguma vez", que e o que as colunas do
                // contato diziam. O MAX pega o mais recente, para a tela mostrar a data certa.
                GanhoEm = c.Negociacoes
                    .Where(n => n.Status != StatusNegociacao.Cancelada)
                    .Max(n => n.GanhaEm),
                PerdidoEm = c.Negociacoes.Max(n => n.PerdidaEm),

                c.CriadoEm,
                // UMA subconsulta correlacionada, não três: `uq_conversas_contato` é único por
                // contato_id, então é um lookup de índice por linha. Três subconsultas separadas
                // (uma por campo) fariam três lookups para trazer a mesma linha.
                Conversa = db.Conversas
                    .Where(v => v.ContatoId == c.Id)
                    .Select(v => new { v.Id, v.AguardandoDesde, v.NaoLidas })
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        // Daqui para baixo é só remontagem de campo — nada de filtro, ordenação ou agregação.
        var itens = linhas.Select(c => new ContatoResumo(
            c.Id, c.Nome, c.Telefone, c.Email, c.Origem.ToString().ToLower(),
            // `?? ""` porque o subselect e anulavel em tese. Hoje todo contato tem ao menos
            // uma negociacao, mas isso e invariante de CODIGO e nao do banco — e o bloco que vem
            // depois deste (o lead que chega sem funil) a derruba de proposito. Vazio degrada
            // para uma linha sem etapa; um nulo declarado como nao-nulo seria pior.
            c.EtapaId, c.EtapaNome ?? "", c.OrdemKanban,
            c.ResponsavelId, c.ResponsavelNome,
            c.Valor, c.GanhoEm, c.PerdidoEm, c.CriadoEm,
            c.Conversa?.Id, c.Conversa?.AguardandoDesde, c.Conversa?.NaoLidas ?? 0)).ToList();

        return new Pagina<ContatoResumo>(total, pagina, tamanho, itens);
    }

    public async Task<ContatoDetalhe> DetalheAsync(long id, CancellationToken ct)
    {
        var c = await db.Contatos.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.Nome, x.Telefone, x.Email, x.Origem,

                // A posicao, o valor e o motivo sao do NEGOCIO vigente (E4e) — o aberto, ou o
                // mais recente quando nao ha nenhum aberto. Mesma regra da lista e da caixa.
                EtapaId = x.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.EtapaId).FirstOrDefault(),
                EtapaNome = x.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.Etapa.Nome).FirstOrDefault(),
                OrdemKanban = x.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.OrdemKanban).FirstOrDefault(),
                Valor = x.Negociacoes
                    .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
                    .ThenByDescending(n => n.Id)
                    .Select(n => n.Valor).FirstOrDefault(),

                x.ResponsavelId, ResponsavelNome = x.Responsavel == null ? null : x.Responsavel.Nome,

                GanhoEm = x.Negociacoes
                    .Where(n => n.Status != StatusNegociacao.Cancelada)
                    .Max(n => n.GanhaEm),
                PerdidoEm = x.Negociacoes.Max(n => n.PerdidaEm),

                x.CriadoEm,
                x.OrigemDetalhe, x.Observacoes, x.AnonimizadoEm,

                // O motivo da ULTIMA perda. Era coluna do contato; agora cada negocio perdido
                // tem o seu, e a tela do contato mostra o mais recente.
                MotivoPerda = x.Negociacoes
                    .Where(n => n.PerdidaEm != null)
                    .OrderByDescending(n => n.PerdidaEm)
                    .Select(n => n.MotivoPerda).FirstOrDefault(),
                Conversa = db.Conversas
                    .Where(v => v.ContatoId == x.Id)
                    .Select(v => new
                    {
                        v.Id, v.AguardandoDesde, v.NaoLidas, v.UltimaMensagemEm,
                        // O nome sai do CADASTRO do canal, e não de uma cópia em texto: campanha
                        // renomeada tem que aparecer renomeada aqui, porque a pergunta é sobre a
                        // campanha viva. (`origem_detalhe`, ao lado, é o oposto de propósito.)
                        CanalDoCiclo = v.CanalCiclo == null ? null : v.CanalCiclo.Nome
                    })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Contato não encontrado.");

        var lembretes = await db.Lembretes.AsNoTracking()
            .Where(l => l.ContatoId == id)
            .OrderByDescending(l => l.DataAlvo).ThenByDescending(l => l.Id)
            .Select(l => new LembreteDto(
                l.Id, l.ContatoId, l.Contato.Nome, l.ConversaId,
                l.Origem.ToString().ToLower(), l.Status.ToString().ToLower(),
                l.DataAlvo, l.HoraAlvo, l.Titulo, l.Observacao, l.EnviaMensagem,
                l.ResponsavelId, l.Responsavel == null ? null : l.Responsavel.Nome, l.ConcluidoEm))
            .ToListAsync(ct);

        var resumo = new ContatoResumo(
            c.Id, c.Nome, c.Telefone, c.Email, c.Origem.ToString().ToLower(),
            c.EtapaId, c.EtapaNome ?? "", c.OrdemKanban,
            c.ResponsavelId, c.ResponsavelNome,
            c.Valor, c.GanhoEm, c.PerdidoEm, c.CriadoEm,
            c.Conversa?.Id, c.Conversa?.AguardandoDesde, c.Conversa?.NaoLidas ?? 0);

        return new ContatoDetalhe(
            resumo, await PipelineDaEtapaAsync(c.EtapaId, ct),
            c.OrigemDetalhe, c.Observacoes, c.MotivoPerda, c.AnonimizadoEm,
            c.Conversa?.UltimaMensagemEm, c.Conversa?.CanalDoCiclo, lembretes);
    }

    /// <summary>Busca por nome OU telefone.
    ///
    /// O telefone é buscado pelos DÍGITOS: o vendedor digita "(84) 98888" e a coluna guarda
    /// "5584988887777". Sem tirar a máscara, procurar pelo que está escrito na tela não acha
    /// nada — e o vendedor conclui que o contato não existe.</summary>
    private static IQueryable<Contato> AplicarBusca(IQueryable<Contato> q, string? busca)
    {
        if (string.IsNullOrWhiteSpace(busca)) return q;

        var texto = busca.Trim().ToLower();
        var digitos = new string(busca.Where(char.IsDigit).ToArray());

        // Menos de 3 dígitos vira ruído: "8" casaria com metade da base.
        return digitos.Length >= 3
            ? q.Where(c => c.Nome.ToLower().Contains(texto) || c.Telefone.Contains(digitos))
            : q.Where(c => c.Nome.ToLower().Contains(texto));
    }

    // ==================================================================== escrita
    public async Task<long> CriarAsync(NovoContato novo, CancellationToken ct)
    {
        var nome = Exigir(novo.Nome, "Informe o nome do contato.");
        var telefone = CanonicalizarTelefone(novo.Telefone);

        // O índice é PARCIAL (`WHERE anonimizado_em IS NULL`), então esta checagem tem que
        // repetir o predicado — senão um contato anonimizado com o mesmo número bloquearia o
        // cadastro de um novo, e a mensagem de erro seria uma mentira.
        if (await db.Contatos.AnyAsync(c => c.Telefone == telefone && c.AnonimizadoEm == null, ct))
            throw new RegraDeNegocioException(
                "Já existe um contato com este telefone.", conflito: true);

        var etapaId = novo.EtapaId is { } informada
            ? await ValidarEtapaAsync(informada, ct)
            // Contato criado a mao, sem etapa: entra pela pipeline PADRAO.
            : await PrimeiraEtapaAsync(await PipelinePadraoAsync(ct), ct);

        await ValidarResponsavelAsync(novo.ResponsavelId, ct);

        var contato = new Contato
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Telefone = telefone,
            Email = Vazio(novo.Email),
            Origem = ParseOrigem(novo.Origem),
            OrigemDetalhe = Vazio(novo.OrigemDetalhe),
            EtapaId = etapaId,
            ResponsavelId = novo.ResponsavelId,
            // `Valor` NAO ENTRA AQUI (E4e/3b): ele e do negocio, e desce para a negociacao logo
            // abaixo. A coluna `contatos.valor` ainda existe, e e exatamente por isso que a
            // linha tinha de sair — deixa-la faria o valor ser gravado nos DOIS lugares, que e
            // a divergencia que o bloco inteiro veio eliminar.
            Observacoes = Vazio(novo.Observacoes),
            // Entra no FIM da coluna. Lead novo no topo empurraria para baixo o que o vendedor já
            // estava trabalhando, e a ordem do quadro é dele, não do sistema.
            OrdemKanban = await ProximaOrdemAsync(etapaId, ct)
        };

        db.Contatos.Add(contato);

        // A negociacao aberta nasce junto com o contato, no MESMO SaveChanges. Separar abriria
        // uma janela com contato sem card. E e ela que recebe o valor informado no cadastro.
        db.Negociacoes.Add(
            await AberturaDeNegociacao.NovaAsync(db, contato, novo.Valor, null, ct));

        await db.SaveChangesAsync(ct);

        // ===================== O EVENTO DE CRIAÇÃO VEM DEPOIS =====================
        // Só aqui a chave existe: antes do INSERT o id é 0, e gravar a trilha com zero produziria
        // eventos órfãos que nunca aparecem na linha do tempo de ninguém.
        //
        // O custo é um segundo comando, FORA da transação do primeiro. Se ele falhar, o contato
        // existe sem o evento "criou" — e isso é o lado barato do erro: a criação também está em
        // `criado_em`, enquanto uma edição perdida não tem outra fonte.
        // ==========================================================================
        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Criou,
            new Dictionary<string, AlteracaoValor>
            {
                ["nome"] = new(null, contato.Nome),
                ["telefone"] = new(null, contato.Telefone)
            });
        await db.SaveChangesAsync(ct);

        // DEPOIS do commit, e sem esperar entrega nenhuma: publicar é um INSERT na fila de saída.
        // Ver IPublicadorEventos — quem posta é a rodada de drenagem.
        await eventos.PublicarContatoAsync(EventoWebhook.LeadCriado, contato, ct: ct);

        return contato.Id;
    }

    public async Task AtualizarAsync(long id, EditarContato dados, CancellationToken ct)
    {
        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        var telefone = CanonicalizarTelefone(dados.Telefone);
        if (telefone != contato.Telefone &&
            await db.Contatos.AnyAsync(
                c => c.Telefone == telefone && c.Id != id && c.AnonimizadoEm == null, ct))
            throw new RegraDeNegocioException(
                "Já existe outro contato com este telefone.", conflito: true);

        await ValidarResponsavelAsync(dados.ResponsavelId, ct);

        contato.Nome = Exigir(dados.Nome, "Informe o nome do contato.");
        contato.Telefone = telefone;
        contato.Email = Vazio(dados.Email);
        contato.Origem = ParseOrigem(dados.Origem);
        contato.OrigemDetalhe = Vazio(dados.OrigemDetalhe);
        contato.ResponsavelId = dados.ResponsavelId;
        // `valor` NAO e mais do contato: e do negocio aberto, logo abaixo (E4e).
        contato.Observacoes = Vazio(dados.Observacoes);

        // ===================== O ESPELHO (E4), E ELE FALTAVA =====================
        // Desde que o quadro passou a ler `negociacoes`, `valor` e `responsavel` do CARD saem de
        // la. Editar o contato mudava so `contatos`, e o card continuava mostrando o numero
        // velho e o avatar velho — o dono salvava, voltava ao quadro e nada tinha mudado.
        //
        // So a ABERTA acompanha: a ganha guarda o valor FECHADO e o vendedor que fechou, e
        // reescreve-los aqui mudaria historico de faturamento a partir de uma tela de cadastro.
        // =======================================================================
        var aberta = await db.Negociacoes
            .Where(n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Aberta)
            .OrderByDescending(n => n.Id)
            .FirstOrDefaultAsync(ct);

        if (aberta is not null)
        {
            // ⚠️ A TRILHA DEIXOU DE REGISTRAR MUDANCA DE VALOR NESTA TELA, e isso e uma
            // PERDA conhecida, nao um descuido.
            //
            // O interceptor monta o diff lendo o ChangeTracker do CONTATO, e `valor` deixou de
            // morar la. Declarar explicitamente nao resolve: `Declarar` cria um evento NOVO, e o
            // resultado foram dois `Editou` para um clique.
            //
            // O conserto certo e auditar `negociacoes` — `EntidadeAuditada` ainda nao tem membro
            // para ela. Fica anotado: enquanto isso, "quem mudou o valor" nao tem resposta na
            // linha do tempo do contato.
            aberta.Valor = dados.Valor;
            aberta.ResponsavelId = dados.ResponsavelId;
        }

        // A ETAPA NÃO SE MUDA POR AQUI, de propósito: mover é operação de funil, com cálculo de
        // ordem e a recusa da etapa de ganho. Aceitar etapa neste PUT abriria um segundo caminho
        // que não faz nada disso — exatamente o buraco que este bloco veio fechar.
        //
        // UM evento com TODOS os campos alterados (AUD-1). O interceptor lê o ChangeTracker e
        // monta o diff — seis colunas mexidas num clique são um fato, não seis.
        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Editou);

        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== estado terminal
    public async Task MarcarGanhoAsync(long id, decimal valor, long? canalId, CancellationToken ct)
    {
        if (valor <= 0)
            throw new RegraDeNegocioException("Informe o valor da venda.");

        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        // ===================== A PERGUNTA MUDOU DE LUGAR (E4e) =====================
        // Era "o contato esta carimbado como ganho?". Agora e "ha um negocio ABERTO para
        // fechar?" — que e a mesma pergunta feita onde o dado mora, e responde melhor: a
        // mensagem sai diferente conforme o motivo de nao haver.
        // ========================================================================
        var emAberto = await db.Negociacoes
            .Where(n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Aberta)
            .OrderByDescending(n => n.Id)
            .FirstOrDefaultAsync(ct);

        if (emAberto is null)
        {
            var jaGanhou = await db.Negociacoes.AsNoTracking().AnyAsync(
                n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Ganha, ct);

            if (jaGanhou)
                throw new RegraDeNegocioException(
                    "Esta venda já está marcada como fechada.", conflito: true);

            throw new RegraDeNegocioException(
                "Este contato não tem negócio em aberto. Reabra antes de registrar a venda.",
                conflito: true);
        }


        // ===================== DE ONDE VEIO ESTA COMPRA (NEG-3) =====================
        // Precedencia, do mais confiavel para o menos:
        //   1. o canal informado no fechamento — alguem olhou e confirmou;
        //   2. `conversas.canal_ciclo_id` — o codigo que chegou numa mensagem DESTE ciclo;
        //   3. nulo.
        //
        // ⚠️ NUNCA o canal do cadastro original. A campanha que trouxe o cliente em marco nao e
        // a que trouxe a compra de agosto, e `contatos.origem` guarda a primeira de proposito
        // (NEG-1). Herdar dali faria o relatorio creditar receita nova a campanha velha.
        //
        // A validacao do canal informado nao e cerimonia: a FK de `vendas.canal_id` e simples,
        // sem `empresa_id`, entao so esta leitura — que passa pelo filtro de tenant — impede que
        // um id vindo do corpo da requisicao aponte para o canal de outra empresa.
        if (canalId is { } informado
            && !await db.CanaisCaptacao.AsNoTracking().AnyAsync(c => c.Id == informado, ct))
            throw new RegraDeNegocioException("Canal de captação não encontrado.");

        // A conversa mais recente que tem canal do ciclo. Mais de uma conversa por contato so
        // acontece com mais de uma conexao; nesse caso vale a que recebeu mensagem por ultimo.
        var canalDaVenda = canalId ?? await db.Conversas.AsNoTracking()
            .Where(c => c.ContatoId == contato.Id && c.CanalCicloId != null)
            .OrderByDescending(c => c.UltimaMensagemEm)
            .Select(c => c.CanalCicloId)
            .FirstOrDefaultAsync(ct);

        var agora = relogio.GetUtcNow().UtcDateTime;


        // A SEGUNDA METADE DA PORTA ÚNICA: carimbar e mover na MESMA operação. É isto que permite
        // ao cliente tratar "arrastar para Venda" e "clicar em Venda fechada" como a mesma coisa.
        var etapaAnterior = contato.EtapaId;

        // ⚠️ A ETAPA DE GANHO **DA PIPELINE DESTE CONTATO**, nao "a" etapa de ganho.
        // Enquanto havia uma pipeline so, `Where(e => e.EGanho)` tinha uma resposta unica. Com
        // varias, ele devolve a de QUALQUER funil — e o contato de "Atacado" seria carimbado como
        // ganho e jogado na coluna Venda de "Pos-venda". Sem erro nenhum: o card so aparece no
        // quadro errado.
        var pipelineDoContato = await PipelineDaEtapaAsync(contato.EtapaId, ct);
        var etapaGanho = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.EGanho && e.PipelineId == pipelineDoContato)
            .Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);

        if (etapaGanho is { } destino && destino != contato.EtapaId)
        {
            contato.EtapaId = destino;
            contato.OrdemKanban = await ProximaOrdemAsync(destino, ct);
        }

        // ===================== UMA LINHA SO, E NAO DUAS (NEG-1 -> E4e) =====================
        // O NEG-1 precisou de duas: a COLUNA em `contatos` dizia o estado atual e a LINHA em
        // `vendas` registrava o que aconteceu. A divisao existia porque havia um card por
        // contato — o carimbo tinha de morar em algum lugar, e o unico lugar era o contato.
        //
        // Agora as duas metades sao a mesma linha. A negociacao que estava aberta vira ganha, com
        // valor, data e canal: e o registro da venda E a posicao no quadro ao mesmo tempo.
        //
        // ⚠️ Nao existe mais a janela que o comentario antigo temia — "carimbo sem faturamento,
        // ou faturamento sem carimbo". Nao ha o que dessincronizar quando ha uma linha so.
        //
        // `etapa_id` congela a etapa de ganho do momento: a empresa pode renomea-la depois, e um
        // relatorio do mes passado precisa dizer o que estava escrito la.
        // ================================================================================
        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Ganhou);

        // ===================== `dias = 0` CONCLUI NA HORA (NEG-2) =====================
        // Padaria, salao, loja de balcao: a venda nasce e termina no mesmo atendimento. Deixar
        // isso so para a rodada diaria manteria o card na coluna ate as 8h do dia seguinte —
        // que e justamente o acumulo que o bloco veio resolver.
        //
        // `ConcluidaPor` NULL: ninguem clicou em concluir, foi a regra da empresa. Mesma razao
        // do `ator = Sistema` da trilha.
        // =============================================================================
        var diasParaConcluir = await db.Empresas.AsNoTracking()
            .Select(e => e.DiasParaConcluirVenda).FirstOrDefaultAsync(ct);

        // A MESMA linha muda de estado: a negociacao aberta vira ganha. Nao e uma linha nova —
        // o negocio e o mesmo, so acabou. Criar outra aqui dobraria o card no quadro.
        var negociacao = emAberto;
        negociacao.Status = StatusNegociacao.Ganha;
        negociacao.Valor = valor;
        negociacao.GanhaEm = agora;
        negociacao.EtapaId = etapaGanho ?? contato.EtapaId;
        negociacao.OrdemKanban = contato.OrdemKanban;

        // ⚠️ 0 NAO E USUARIO. Sem sessao (job, migracao) o contexto traz zero, e grava-lo aqui
        // violaria a FK do responsavel — o fechamento inteiro falharia. Encontrado pelo teste de
        // ator=sistema do AUD-1, quando isto ainda era `vendas.responsavel_id`.
        negociacao.ResponsavelId = contexto.UsuarioId == 0 ? null : contexto.UsuarioId;
        negociacao.CanalCicloId = canalDaVenda;

        if (diasParaConcluir == 0)
        {
            negociacao.Status = StatusNegociacao.Concluida;
            negociacao.ConcluidaEm = agora;
            negociacao.ConcluidaPor = null;
        }

        await db.SaveChangesAsync(ct);

        // A trilha da venda so DEPOIS do save, quando o id existe — antes do INSERT ele e zero,
        // e o evento sairia apontando para lugar nenhum. `EntidadeAuditada.Venda` continua sendo
        // o rotulo: para quem le a linha do tempo, o fato e "uma venda", nao "uma negociacao
        // mudou de status".
        trilha.Declarar(EntidadeAuditada.Venda, negociacao.Id, AcaoAuditoria.Criou,
            new Dictionary<string, AlteracaoValor> { ["valor"] = new(null, valor) });

        if (diasParaConcluir == 0)
            trilha.Declarar(EntidadeAuditada.Venda, negociacao.Id, AcaoAuditoria.Concluiu);
        await db.SaveChangesAsync(ct);

        // NEG-3: com prazo zero a venda ja nasceu concluida, entao o ciclo acabou AQUI — a
        // conversa volta para a fila e o canal e apagado, exatamente como no botao de concluir.
        // Sem isto o balcao (padaria, salao) nunca liberaria conversa nenhuma, que e justamente
        // quem mais tem cliente que volta.
        if (diasParaConcluir == 0)
            await LiberacaoDeCiclo.ExecutarAsync(db, [contato.Id], agora, ct);

        // UM evento, não dois. Carimbar o ganho move de etapa junto, mas quem recebe `venda.fechada`
        // não precisa também de um `lead.movido` da mesma ação — seriam dois eventos para uma coisa
        // só, e o receptor teria que adivinhar que são o mesmo fato. A etapa anterior vai DENTRO
        // do payload de `venda.fechada`, que é onde ela é útil.
        await eventos.PublicarContatoAsync(EventoWebhook.VendaFechada, contato, etapaAnterior, ct);
    }

    /// <summary>DUAS leituras e nenhum JOIN entre elas: a lista de canais é da empresa e o
    /// detectado é da conversa. Juntá-las num `LEFT JOIN` traria o canal detectado só quando ele
    /// também estivesse ativo — e o caso que importa é justamente o contrário.</summary>
    public async Task<CanaisDoFechamento> CanaisDoFechamentoAsync(
        long contatoId, CancellationToken ct)
    {
        // O filtro de tenant cuida do recorte; contato de outra empresa não tem conversa aqui e
        // o detectado sai nulo, que é a resposta segura.
        var detectado = await db.Conversas.AsNoTracking()
            .Where(c => c.ContatoId == contatoId && c.CanalCicloId != null)
            .OrderByDescending(c => c.UltimaMensagemEm)
            .Select(c => c.CanalCicloId)
            .FirstOrDefaultAsync(ct);

        var canais = await db.CanaisCaptacao.AsNoTracking()
            .Where(c => c.Ativo || c.Id == detectado)
            .OrderBy(c => c.Nome)
            .Select(c => new OpcaoCanalFechamento(c.Id, c.Nome, c.Ativo))
            .ToListAsync(ct);

        return new CanaisDoFechamento(detectado, canais);
    }

    public async Task MarcarPerdidoAsync(long id, string motivo, CancellationToken ct)
    {
        var texto = Exigir(motivo, "Informe o motivo da perda.");

        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        // Mesma troca do ganho: a pergunta e "ha negocio aberto para perder?" (E4e).
        var perdida = await db.Negociacoes
            .Where(n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Aberta)
            .OrderByDescending(n => n.Id)
            .FirstOrDefaultAsync(ct);

        if (perdida is null)
        {
            var jaPerdeu = await db.Negociacoes.AsNoTracking().AnyAsync(
                n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Perdida, ct);

            if (jaPerdeu)
                throw new RegraDeNegocioException(
                    "Este contato já está marcado como perdido.", conflito: true);

            // Recusar aqui, com instrucao, e melhor que limpar o ganho por baixo do pano: o
            // vendedor perderia o registro da venda, e ninguem entenderia depois por que o
            // historico sumiu.
            throw new RegraDeNegocioException(
                "Este contato está marcado como venda fechada. Reabra antes de marcar como perdido.",
                conflito: true);
        }

        perdida.Status = StatusNegociacao.Perdida;
        perdida.PerdidaEm = relogio.GetUtcNow().UtcDateTime;
        perdida.MotivoPerda = texto;
        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Perdeu);
        // NÃO muda de etapa: ix_contatos_kanban filtra `perdido_em IS NULL`, então o card sai do
        // quadro sozinho — e preservar a etapa registra ONDE a negociação morreu.
        await db.SaveChangesAsync(ct);

        await eventos.PublicarContatoAsync(EventoWebhook.VendaPerdida, contato, ct: ct);
    }

    public async Task ReabrirAsync(long id, CancellationToken ct)
    {
        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        // E4e: "ja esta em aberto" passou a ser "ja tem negocio aberto".
        if (await db.Negociacoes.AsNoTracking().AnyAsync(
                n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Aberta, ct))
            throw new RegraDeNegocioException("Este contato já está em aberto.", conflito: true);

        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Reabriu);

        var estavaGanho = await db.Negociacoes.AsNoTracking().AnyAsync(
            n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Ganha, ct);

        // ⚠️ `vendas` NÃO É TOCADA AQUI (NEG-1). Reabrir é "o cliente voltou", e o que já foi
        // faturado continua faturado. Era exatamente esta linha que faltava: sem a tabela, limpar
        // `ganho_em` apagava a venda anterior do dashboard, e o faturamento de um mês fechado
        // mudava sozinho. Quem desfaz uma venda errada é `ServicoVendas.CancelarAsync`.

        // Reabrir devolve o card ao quadro. Se a coluna atual for a de ganho, ele ficaria lá SEM
        // `ganho_em` — o estado divergente que a porta única existe para impedir.
        var etapaEhGanho = await db.EtapasFunil.AsNoTracking()
            .AnyAsync(e => e.Id == contato.EtapaId && e.EGanho, ct);

        if (etapaEhGanho)
        {
            // Volta para o inicio DO PROPRIO funil, nao do funil padrao: reabrir e retomar a
            // negociacao onde ela estava, e mudar de pipeline nesse gesto seria uma surpresa.
            var primeira = await PrimeiraEtapaAsync(
                await PipelineDaEtapaAsync(contato.EtapaId, ct), ct);
            contato.EtapaId = primeira;
            contato.OrdemKanban = await ProximaOrdemAsync(primeira, ct);
        }

        // ===================== O ESPELHO (E4b), E OS DOIS CASOS SAO DIFERENTES =====================
        // Reabrir uma PERDA desfaz a perda: a mesma negociacao volta a ser aberta, exatamente
        // como `perdido_em` volta a ser nulo.
        //
        // ⚠️ Reabrir um GANHO NAO desfaz o ganho. `vendas` nao e tocada aqui de proposito (o
        // comentario acima explica: o que ja foi faturado continua faturado), entao a negociacao
        // ganha TAMBEM fica — e a rodada nova e uma linha nova. Rebaixar a ganha para aberta
        // faria o faturamento de um mes fechado mudar sozinho, que e o defeito que a tabela
        // `vendas` foi criada para corrigir.
        //
        // Quem desfaz uma venda errada continua sendo `ServicoVendas.CancelarAsync`.
        // ==========================================================================================
        if (estavaGanho)
        {
            // Rodada nova comeca SEM valor: o do negocio anterior era o preco daquela venda,
            // e herda-lo aqui poria um numero na proposta nova que ninguem digitou.
            db.Negociacoes.Add(await AberturaDeNegociacao.NovaAsync(db, contato, null, null, ct));
        }
        else
        {
            var reaberta = await db.Negociacoes
                .Where(n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Perdida)
                .OrderByDescending(n => n.Id)
                .FirstOrDefaultAsync(ct);

            if (reaberta is null)
            {
                db.Negociacoes.Add(
                    await AberturaDeNegociacao.NovaAsync(db, contato, null, null, ct));
            }
            else
            {
                reaberta.Status = StatusNegociacao.Aberta;
                reaberta.PerdidaEm = null;
                reaberta.MotivoPerda = null;
                reaberta.EtapaId = contato.EtapaId;
                reaberta.OrdemKanban = contato.OrdemKanban;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== LGPD
    /// <summary>Zera a PII e preserva o histórico.
    ///
    /// ===================== O PROBLEMA DO TELEFONE =====================
    /// `telefone` é NOT NULL, então não dá para apagar. O substituto é `ANON-{id}`: determinístico
    /// (a mesma linha sempre produz o mesmo valor, então reexecutar é idempotente) e único (o id
    /// é único).
    ///
    /// Vale registrar que a colisão entre dois anonimizados NÃO ocorreria nem sem o marcador: o
    /// índice `uq_contatos_telefone` é PARCIAL, com `WHERE anonimizado_em IS NULL`, e a linha sai
    /// do índice no instante em que é anonimizada. O marcador não está aqui para satisfazer a
    /// constraint — está porque o telefone É a PII, e apagá-lo é o ponto da operação.
    /// ==================================================================</summary>
    public async Task AnonimizarAsync(long id, CancellationToken ct)
    {
        var contato = await CarregarAsync(id, ct);

        if (contato.AnonimizadoEm is not null)
            throw new RegraDeNegocioException("Este contato já foi anonimizado.", conflito: true);

        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Anonimizou);

        contato.Nome = NomeAnonimo;
        contato.Telefone = $"ANON-{contato.Id}";
        contato.Email = null;
        contato.Observacoes = null;
        contato.OrigemDetalhe = null;
        contato.AnonimizadoEm = relogio.GetUtcNow().UtcDateTime;

        // PRESERVADOS de propósito: etapa, ordem, valor, ganho_em, perdido_em, motivo_perda,
        // responsável — e, por não serem tocados, a conversa, as mensagens e os lembretes. O
        // dashboard continua contando a venda; o que sumiu foi quem era a pessoa.
        await db.SaveChangesAsync(ct);

        await MascararTrilhaAsync(contato.Id, ct);
    }

    /// <summary>===================== A TRILHA TAMBÉM GUARDA PII (AUD-1) =====================
    ///
    /// A auditoria registra valor ANTIGO. Valor antigo de contato é nome, telefone, e-mail e
    /// observação — e o próprio evento de anonimização grava `nome: "João" → "Contato
    /// anonimizado"`.
    ///
    /// Se essas linhas ficassem, **a anonimização não teria acontecido**: o dado pessoal
    /// continuaria no banco, só teria mudado de tabela. Um pedido de titular respondido com "foi
    /// removido" seria falso.
    ///
    /// O EVENTO FICA, o dado sai. Continua registrado que alguém editou o nome em tal dia — o que
    /// preserva a trilha como prova de conformidade —, e o valor vira `[removido]`.
    ///
    /// DEPOIS do SaveChanges, de propósito: a linha do próprio `Anonimizou` precisa existir para
    /// ser mascarada. Rodar antes deixaria justamente o evento mais sensível intacto.
    ///
    /// jsonb reconstruído chave a chave: as que não são PII (etapa, responsável) permanecem
    /// legíveis. Apagar `alteracoes` inteiro seria mais simples e destruiria a utilidade da
    /// trilha para tudo que não é dado pessoal.
    /// ==============================================================================</summary>
    private Task MascararTrilhaAsync(long contatoId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("""
            UPDATE auditoria a
               SET alteracoes = COALESCE((
                     SELECT jsonb_object_agg(
                              e.chave,
                              CASE WHEN e.chave IN ('nome','telefone','email','observacoes','origemDetalhe')
                                   THEN jsonb_build_object('antes', {2}::text, 'depois', {2}::text)
                                   ELSE e.valor END)
                       FROM jsonb_each(a.alteracoes) AS e(chave, valor)), jsonb_build_object())
             WHERE a.empresa_id = {0}
               AND a.entidade = 'Contato'
               AND a.entidade_id = {1}
            """, [contexto.EmpresaId, contatoId, Auditoria.Mascarado], ct);

    // ==================================================================== apoio
    private async Task<Contato> CarregarAsync(long id, CancellationToken ct) =>
        await db.Contatos.FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new RegraDeNegocioException("Contato não encontrado.");

    private static void RecusarSeAnonimizado(Contato c)
    {
        if (c.AnonimizadoEm is not null)
            throw new RegraDeNegocioException(
                "Este contato foi anonimizado e não pode mais ser alterado.", conflito: true);
    }

    /// <summary>Valida que a etapa é DESTA empresa e que não é a de ganho.
    ///
    /// O query filter protege a LEITURA; um id de etapa de outro tenant vindo do cliente precisa
    /// de checagem explícita. Como `db.EtapasFunil` já está filtrado, "não encontrada" e "é de
    /// outra empresa" caem no mesmo ramo — que é exatamente o que um tenant deve ver do outro.</summary>
    private async Task<long> ValidarEtapaAsync(long etapaId, CancellationToken ct)
    {
        var etapa = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.Id == etapaId)
            .Select(e => new { e.Id, e.EGanho })
            .FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Etapa não encontrada.");

        if (etapa.EGanho)
            throw new RegraDeNegocioException(
                "Para colocar um contato na etapa de venda, registre a venda com o valor fechado.",
                conflito: true);

        return etapa.Id;
    }

    private async Task ValidarResponsavelAsync(long? responsavelId, CancellationToken ct)
    {
        if (responsavelId is not { } id) return;

        // Mesma lógica da etapa: o filtro global já restringe `db.Usuarios` ao tenant.
        if (!await db.Usuarios.AnyAsync(u => u.Id == id, ct))
            throw new RegraDeNegocioException("Responsável não encontrado na equipe.");
    }

    /// <summary>A etapa por onde o lead entra NUMA pipeline — a de menor ordem.
    ///
    /// ⚠️ O `pipelineId` nao e enfeite. Sem ele, `OrderBy(e => e.Ordem).First()` devolve a etapa 1
    /// de um funil qualquer, e o contato nasce no quadro errado. Com uma pipeline so a pergunta
    /// tinha uma resposta; com varias ela precisa dizer de qual.</summary>
    private async Task<long> PrimeiraEtapaAsync(long pipelineId, CancellationToken ct) =>
        await db.EtapasFunil.AsNoTracking()
            .Where(e => e.PipelineId == pipelineId)
            .OrderBy(e => e.Ordem).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException(
            "Esta empresa não tem funil configurado. Fale com o suporte.");

    /// <summary>A pipeline a que uma etapa pertence. E assim que o contato "sabe" em qual funil
    /// esta: ele nao guarda a pipeline, guarda a etapa — e a etapa guarda a pipeline.</summary>
    private async Task<long> PipelineDaEtapaAsync(long etapaId, CancellationToken ct) =>
        await db.EtapasFunil.AsNoTracking()
            .Where(e => e.Id == etapaId).Select(e => (long?)e.PipelineId).FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException("Etapa não encontrada.");

    /// <summary>A pipeline padrao — onde entra quem nao veio de lugar nenhum.
    ///
    /// ⚠️ E O CAMINHO DE TODO LEAD NOVO. Por enquanto e sempre a marcada como padrao; o codigo de
    /// campanha decidir a pipeline e o bloco seguinte.</summary>
    private async Task<long> PipelinePadraoAsync(CancellationToken ct) =>
        await db.Pipelines.AsNoTracking()
            .OrderByDescending(p => p.Padrao).ThenBy(p => p.Ordem).ThenBy(p => p.Id)
            .Select(p => (long?)p.Id).FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException(
            "Esta empresa não tem funil configurado. Fale com o suporte.");

    /// <summary>A ordem do FIM da coluna. MAX no SQL, nunca varrendo a coluna em memória.</summary>
    private async Task<decimal> ProximaOrdemAsync(long etapaId, CancellationToken ct)
    {
        // E4e: a posicao mora na negociacao. `NoQuadro` faz o papel do `perdido_em IS NULL` de
        // antes — o que saiu do quadro nao disputa posicao nele.
        var ultima = await db.Negociacoes.AsNoTracking()
            .Where(n => n.EtapaId == etapaId)
            .Where(RegrasNegociacao.NoQuadro)
            .MaxAsync(n => (decimal?)n.OrdemKanban, ct);

        return (ultima ?? 0m) + 1m;
    }

    private static string Exigir(string? valor, string mensagem)
    {
        var t = (valor ?? "").Trim();
        if (t.Length == 0) throw new RegraDeNegocioException(mensagem);
        return t;
    }

    private static string? Vazio(string? valor)
    {
        var t = (valor ?? "").Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>Canonicaliza e VALIDA. Falhar alto aqui é deliberado: telefone que não casa com o
    /// formato do WhatsApp nunca vai receber mensagem nenhuma, e o sintoma seria silencioso — o
    /// contato existe, aparece na tela, e simplesmente nada chega nele.</summary>
    private static string CanonicalizarTelefone(string? bruto)
    {
        var informado = Exigir(bruto, "Informe o telefone do contato.");
        var canonico = CanonicalizadorTelefone.Canonicalizar(informado);

        if (!CanonicalizadorTelefone.EhValido(canonico))
            throw new RegraDeNegocioException(
                "Telefone inválido. Use DDD e número, como (84) 98888-7777.");

        return canonico;
    }

    private static OrigemLead ParseOrigem(string? origem) =>
        Enum.TryParse<OrigemLead>(origem, ignoreCase: true, out var o) ? o : OrigemLead.Manual;
}
