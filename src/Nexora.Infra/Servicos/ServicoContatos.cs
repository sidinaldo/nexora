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
    public async Task<PaginaContatos> ListarAsync(
        FiltroContato filtro, string? busca, long? etapaId, long? responsavelId,
        int pagina, int tamanho, CancellationToken ct)
    {
        pagina = Math.Max(pagina, 1);
        tamanho = Math.Clamp(tamanho, 1, 100);

        // Anonimizado NUNCA aparece em lista nem em busca. Ele existe só para o histórico e para
        // as agregações do dashboard.
        var q = db.Contatos.AsNoTracking().Where(c => c.AnonimizadoEm == null);

        // ===================== OS OUTROS RECORTES VEM PRIMEIRO =====================
        // ⚠️ A ORDEM MUDOU, e ela e o ponto: a situacao (a ABA) passou a ser a ULTIMA coisa
        // aplicada, porque as contagens de todas as abas saem daqui — desta consulta, com a
        // busca, a etapa e o responsavel ja dentro e a aba ainda de fora.
        //
        // Sem isso, "Ganhos 48" ficaria parado na tela enquanto a busca diz "Ysia": um numero
        // que nao corresponde a tela nenhuma que o clique possa produzir.
        // =====================================================================
        // A etapa: o contato "esta" na etapa do negocio dele.
        if (etapaId is { } e) q = q.Where(c => c.Negociacoes.Any(n => n.EtapaId == e));
        if (responsavelId is { } r) q = q.Where(c => c.ResponsavelId == r);
        q = AplicarBusca(q, busca);

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
        //
        // ⚠️ OS TRES PREDICADOS SUBIRAM PARA `RegrasNegociacao`, e nao por arrumacao: a tela
        // mostra a CONTAGEM de cada aba ao lado do rotulo, e as contagens logo abaixo citam
        // exatamente as mesmas expressoes. Escritos por extenso nos dois lugares, o numero e a
        // lista divergiriam no primeiro ajuste — e o cliente clicaria em "Ganhos 2" para ver
        // tres linhas.
        // ==========================================================================
        // ⚠️ `filtrada`, E NAO `q = ...`. Reatribuir `q` aqui apagaria a consulta SEM a
        // aba, que e justamente de onde as quatro contagens saem logo abaixo — e todas elas
        // passariam a contar dentro da aba corrente. "Ganhos" diria 0 sempre que a aba ativa
        // fosse "Em aberto", que e o pior dos numeros errados: parece um dado.
        var filtrada = filtro switch
        {
            FiltroContato.Ganhos => q.Where(RegrasNegociacao.ContatoGanho),

            FiltroContato.Perdidos => q.Where(RegrasNegociacao.ContatoPerdido),

            FiltroContato.Todos => q,

            // ⚠️ `ContatoEmAberto` E NAO `Any(Aberta)`, e a diferenca e quem acabou de chegar:
            // sem a segunda metade da regra, o lead vindo da caixa nao aparecia em filtro nenhum
            // — nem em "Abertos", nem em "Ganhos", nem em "Perdidos" — e a tela, que abre em
            // "Abertos", simplesmente nao o mostrava. Ver `RegrasNegociacao.ContatoEmAberto`.
            _ => q.Where(RegrasNegociacao.ContatoEmAberto)
        };

        // ===================== A CONTAGEM DAS ABAS =====================
        // Tres COUNTs a mais por pagina da lista, e eles pagam por si: e o que impede alguem de
        // sumir da tela sem deixar rastro. Cada um usa o MESMO predicado do `switch` acima, e o
        // total da pagina sai daqui tambem — nao ha uma quinta consulta que possa discordar.
        //
        // `Todos` e o COUNT sem recorte de situacao, entao `Abertos + Ganhos + Perdidos` tem de
        // dar exatamente ele. O teste `AS_ABAS_SOMAM_A_BASE_E_CADA_UMA_BATE_COM_A_LISTA` e quem
        // garante — as faixas sao disjuntas por construcao, e "por construcao" ja falhou aqui.
        // ================================================================
        var contagens = new ContagemPorSituacao(
            await q.CountAsync(RegrasNegociacao.ContatoEmAberto, ct),
            await q.CountAsync(RegrasNegociacao.ContatoGanho, ct),
            await q.CountAsync(RegrasNegociacao.ContatoPerdido, ct),
            await q.CountAsync(ct));

        // ⚠️ O TOTAL DA PAGINA SAI DA CONTAGEM, e nao de um `CountAsync` proprio sobre a consulta
        // ja filtrada. Os dois numeros responderiam a mesma pergunta por caminhos diferentes, e
        // "duas representacoes do mesmo fato" e o defeito que este bloco inteiro veio desmontar:
        // a paginacao diria 13 e a aba diria 12, e ninguem saberia qual acreditar.
        var total = filtro switch
        {
            FiltroContato.Ganhos => contagens.Ganhos,
            FiltroContato.Perdidos => contagens.Perdidos,
            FiltroContato.Todos => contagens.Todos,
            _ => contagens.Abertos
        };

        var linhas = await filtrada
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
                    .Select(n => (long?)n.EtapaId).FirstOrDefault(),
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
            // ⚠️ O BLOCO QUE O COMENTARIO ANTIGO ANUNCIAVA CHEGOU (E6). Ele dizia que todo
            // contato ter negociacao era invariante de CODIGO e nao de banco, e que o bloco
            // seguinte a derrubaria de proposito. Derrubou: lead que chega pela caixa nao abre
            // negociacao, entao nao tem etapa — e o `?? ""` saiu junto, porque vazio e nulo
            // dizem coisas diferentes e so o segundo diz "nao esta em funil nenhum".
            c.EtapaId, c.EtapaNome, c.OrdemKanban,
            c.ResponsavelId, c.ResponsavelNome,
            c.Valor, c.GanhoEm, c.PerdidoEm, c.CriadoEm,
            c.Conversa?.Id, c.Conversa?.AguardandoDesde, c.Conversa?.NaoLidas ?? 0)).ToList();

        return new PaginaContatos(total, pagina, tamanho, itens, contagens);
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
                    .Select(n => (long?)n.EtapaId).FirstOrDefault(),
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
            c.EtapaId, c.EtapaNome, c.OrdemKanban,
            c.ResponsavelId, c.ResponsavelNome,
            c.Valor, c.GanhoEm, c.PerdidoEm, c.CriadoEm,
            c.Conversa?.Id, c.Conversa?.AguardandoDesde, c.Conversa?.NaoLidas ?? 0);

        // ⚠️ `c.EtapaId is { }` E O GUARDA CONTRA UM 500. Sem negociacao a etapa vem nula, e
        // `PipelineDaEtapaAsync(0)` lanca "Etapa nao encontrada" — a tela do contato quebraria
        // para todo lead que chegou pela caixa, que e a maioria desde o E6.
        // ===================== OS NEGOCIOS VIVOS, UM POR LINHA =====================
        // ⚠️ SO `Aberta` E `Ganha`: sao os que estao em algum quadro. Perdido e concluido ja tem
        // lugar — o bloco de vendas e a linha do tempo — e misturar responderia outra pergunta.
        //
        // Ordem: os ABERTOS primeiro (e o que se trabalha hoje), depois os ganhos esperando
        // conclusao; dentro de cada grupo, pela ordem do funil no menu. NAO por id — "id nao e
        // relogio" neste banco, e a ordem que importa para quem le e a das pipelines na tela.
        // ========================================================================
        var negocios = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == id)
            .Where(n => n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha)
            .OrderBy(n => n.Status == StatusNegociacao.Aberta ? 0 : 1)
            .ThenBy(n => n.Pipeline.Ordem).ThenBy(n => n.PipelineId)
            .Select(n => new NegocioDoContato(
                n.Id, n.PipelineId, n.Pipeline.Nome, n.EtapaId, n.Etapa.Nome,
                n.Valor, n.Status.ToString().ToLower(), n.GanhaEm, n.Versao,
                n.Etiquetas
                    .OrderBy(x => x.Etiqueta.Nome)
                    .Select(x => new EtiquetaDto(x.Etiqueta.Id, x.Etiqueta.Nome, x.Etiqueta.Cor))
                    .ToList()))
            .ToListAsync(ct);

        return new ContatoDetalhe(
            resumo,
            c.EtapaId is { } etapa ? await PipelineDaEtapaAsync(etapa, ct) : null,
            c.OrigemDetalhe, c.Observacoes, c.MotivoPerda, c.AnonimizadoEm,
            c.Conversa?.UltimaMensagemEm, c.Conversa?.CanalDoCiclo, negocios, lembretes);
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
            ResponsavelId = novo.ResponsavelId,
            // `Valor` NAO ENTRA AQUI (E4e/3b): ele e do negocio, e desce para a negociacao logo
            // abaixo. A coluna `contatos.valor` ainda existe, e e exatamente por isso que a
            // linha tinha de sair — deixa-la faria o valor ser gravado nos DOIS lugares, que e
            // a divergencia que o bloco inteiro veio eliminar.
            Observacoes = Vazio(novo.Observacoes)
        };

        db.Contatos.Add(contato);

        // A negociacao aberta nasce junto com o contato, no MESMO SaveChanges. Separar abriria
        // uma janela com contato sem card. E e ela que recebe o valor informado no cadastro.
        // Entra no FIM da coluna. Lead novo no topo empurraria para baixo o que o vendedor já
        // estava trabalhando, e a ordem do quadro é dele, não do sistema.
        db.Negociacoes.Add(await AberturaDeNegociacao.NovaAsync(
            db, contato, etapaId, await ProximaOrdemAsync(etapaId, ct), novo.Valor, null, ct));

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
    public async Task MarcarGanhoAsync(
        long id, decimal valor, long? canalId, long? negociacaoId, CancellationToken ct)
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
        // ⚠️ QUANDO A TELA DIZ QUAL, E ELA QUE MANDA. Sem o id, o servico escolhe o aberto mais
        // recente — o que era uma resposta enquanto uma pessoa tinha um negocio so, e virou um
        // sorteio quando ela passou a poder estar em dois funis.
        //
        // O `ContatoId` no filtro nao e redundancia: o id vem do corpo da requisicao, e sem ele
        // um negocio de OUTRA pessoa (do mesmo tenant) seria fechado com o valor desta.
        var emAberto = negociacaoId is { } escolhido
            ? await db.Negociacoes.FirstOrDefaultAsync(
                n => n.Id == escolhido && n.ContatoId == contato.Id
                  && n.Status == StatusNegociacao.Aberta, ct)
            : await db.Negociacoes
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


        // A SEGUNDA METADE DA PORTA ÚNICA: fechar e mover na MESMA operação. É isto que permite
        // ao cliente tratar "arrastar para Venda" e "clicar em Venda fechada" como a mesma coisa.
        var etapaAnterior = emAberto.EtapaId;

        // ⚠️ A ETAPA DE GANHO **DA PIPELINE DESTE NEGOCIO**, nao "a" etapa de ganho.
        // Enquanto havia uma pipeline so, `Where(e => e.EGanho)` tinha uma resposta unica. Com
        // varias, ele devolve a de QUALQUER funil — e o negocio de "Atacado" seria jogado na
        // coluna Venda de "Pos-venda". Sem erro nenhum: o card so aparece no quadro errado.
        //
        // ⚠️ A PIPELINE NAO PRECISA MAIS DE CONSULTA (E4e/4): a negociacao ja a guarda. Antes era
        // `PipelineDaEtapaAsync(contato.EtapaId)` — uma ida ao banco para descobrir algo que a
        // propria linha que estamos editando ja sabia.
        var etapaGanho = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.EGanho && e.PipelineId == emAberto.PipelineId)
            .Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);

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
        // ⚠️ `etapaId` VAI JUNTO (E4e/4), pelo mesmo motivo do `MoverAsync`: registrar a venda e
        // a SEGUNDA porta de entrada na coluna de ganho, e o relatorio de funil conta as duas
        // lendo esta chave. Antes ela vinha de graca, do diff de `contatos.etapa_id`.
        //
        // So declara quando a etapa MUDA de verdade: negocio ja parado na coluna de ganho nao
        // "entrou" nela de novo, e contar isso inflaria o relatorio a cada reedicao.
        if (etapaGanho is { } entrou && entrou != etapaAnterior)
            trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Ganhou,
                new Dictionary<string, AlteracaoValor> { ["etapaId"] = new(etapaAnterior, entrou) });
        else
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
        if (etapaGanho is { } destino && destino != negociacao.EtapaId)
        {
            negociacao.EtapaId = destino;
            negociacao.OrdemKanban = await ProximaOrdemAsync(destino, ct);
        }

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

    public async Task MarcarPerdidoAsync(
        long id, string motivo, long? negociacaoId, CancellationToken ct)
    {
        var texto = Exigir(motivo, "Informe o motivo da perda.");

        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        // Mesma troca do ganho: a pergunta e "ha negocio aberto para perder?" (E4e).
        // Mesmo criterio do ganho: quando a tela diz qual, e ela que manda.
        var perdida = negociacaoId is { } escolhida
            ? await db.Negociacoes.FirstOrDefaultAsync(
                n => n.Id == escolhida && n.ContatoId == contato.Id
                  && n.Status == StatusNegociacao.Aberta, ct)
            : await db.Negociacoes
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

    public async Task AbrirNegociacaoAsync(long id, long? pipelineId, CancellationToken ct)
    {
        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        // ===================== O FUNIL ESCOLHIDO MANDA (E6) =====================
        // ⚠️ A LEITURA PASSA PELO FILTRO DE TENANT, e nao e cerimonia: o id vem do CORPO DA
        // REQUISICAO. Sem esta consulta, um id de outra empresa chegaria ate `PrimeiraEtapaAsync`
        // e o negocio nasceria no funil de outro cliente. A FK composta `fk_negociacoes_etapa`
        // pegaria depois, mas como erro de banco — 500 numa tela, em vez de "funil nao
        // encontrado".
        // =======================================================================
        if (pipelineId is { } escolhida
            && !await db.Pipelines.AsNoTracking().AnyAsync(p => p.Id == escolhida, ct))
            throw new RegraDeNegocioException("Funil não encontrado.");

        // ⚠️ `Ganha` OU `Concluida`, e a segunda metade e um conserto. A pergunta aqui e "esta
        // pessoa JA COMPROU em algum funil?", e `Ganha` sozinho era um proxy que parou de valer
        // no instante em que um card por funil passou a EXIGIR concluir o pedido antes de abrir
        // a rodada nova: o vendedor concluia a venda de Pos-venda, clicava em abrir, e o negocio
        // nascia no funil PADRAO — porque nao havia mais nenhuma `ganha` para indicar o caminho.
        //
        // Concluida nao e o fim do relacionamento; e o fim do PEDIDO. Quem comprou em Pos-venda
        // volta para Pos-venda.
        var jaComprou = await db.Negociacoes.AsNoTracking().AnyAsync(
            n => n.ContatoId == contato.Id
              && (n.Status == StatusNegociacao.Ganha || n.Status == StatusNegociacao.Concluida),
            ct);

        // ⚠️ `vendas` NÃO É TOCADA AQUI (NEG-1). Reabrir é "o cliente voltou", e o que já foi
        // faturado continua faturado. Era exatamente esta linha que faltava: sem a tabela, limpar
        // `ganho_em` apagava a venda anterior do dashboard, e o faturamento de um mês fechado
        // mudava sozinho. Quem desfaz uma venda errada é `ServicoVendas.CancelarAsync`.

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
        // ===================== REVIVER OU CRIAR, E SO HA UM CASO DE REVIVER =====================
        // A perda e a UNICA linha que volta ao quadro sendo a mesma: desfazer uma perda e
        // literalmente desfazer, e a etapa onde ela morreu e informacao que ninguem quer perder.
        //
        // ⚠️ MAS SO QUANDO NINGUEM ESCOLHEU FUNIL. Quem passou `pipelineId` esta dizendo para
        // onde quer ir, e ressuscitar a perda noutro lugar contrariaria a escolha em silencio —
        // o pior tipo de surpresa, porque a tela mostraria um funil e o card apareceria noutro.
        var perdida = pipelineId is null
            ? await db.Negociacoes
                .Where(n => n.ContatoId == contato.Id && n.Status == StatusNegociacao.Perdida)
                .OrderByDescending(n => n.Id)
                .FirstOrDefaultAsync(ct)
            : null;

        // ===================== UM ABERTO POR FUNIL, E NAO POR CONTATO =====================
        // ⚠️ AQUI HAVIA `Any(Status == Aberta)` SOBRE O CONTATO INTEIRO, e era a regra de quando
        // o contato ERA o card. Relatado assim: "por que Ysia esta em 2 negociacao e nao posso
        // incluir ela em mais outro pipeline?".
        //
        // A resposta e que ela podia — a regra e que estava velha. O E4 existe justamente para a
        // mesma pessoa poder estar em Vendas, em Pos-venda e num terceiro funil ao mesmo tempo,
        // e a tela do contato ja LISTA esses negocios um por linha.
        //
        // O que continua proibido e o que de fato confunde: DOIS cards da mesma pessoa NO MESMO
        // funil. Ali nao ha como o vendedor saber qual e qual, e mover um deixa o outro para
        // tras sem ninguem perceber.
        //
        // ⚠️ A CHECAGEM VEM DEPOIS DE DECIDIR O FUNIL, e nao antes: sem saber o destino nao ha o
        // que checar. Por isso a resolucao do funil subiu para ca.
        // ==============================================================================
        // ===================== "ESCOLHA POR MIM" TEM DE ESCOLHER UM FUNIL LIVRE =============
        // ⚠️ A PREFERENCIA NAO PERGUNTAVA SE O FUNIL LEMBRADO AINDA CABIA ALGUEM. Relatado
        // assim: "tenho 3 funis e Ysia esta em 2, mas quando tento incluir ela no terceiro pela
        // tela de contato nao permite".
        //
        // A tela so mostra o seletor quando ha MAIS DE UM funil livre — com um so, ela manda
        // `null`, que quer dizer "escolha por mim". A escolha era "o funil da ultima compra",
        // seca: caia em Vendas, que ja tinha uma aberta, e a resposta era 409 apontando um funil
        // que ninguem tinha pedido. O unico funil livre ficava inalcancavel pela tela.
        //
        // A ordem de preferencia e a mesma de antes — a perda revivida, depois a ultima compra,
        // depois o padrao. O que mudou e que cada candidato passa pelo filtro de estar LIVRE, e
        // o ultimo recurso e o primeiro livre na ordem do menu.
        // ====================================================================================
        var ocupados = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == contato.Id)
            .Where(n => n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha)
            .Select(n => n.PipelineId)
            .Distinct()
            .ToListAsync(ct);

        long funilDestino;

        if (pipelineId is { } escolhido)
        {
            // ⚠️ A ESCOLHA EXPLICITA NAO E DESVIADA. Quem apontou o funil quer AQUELE; mandar o
            // negocio para outro seria a tela mostrar uma coisa e o card aparecer noutra. Se ele
            // estiver ocupado, a recusa logo abaixo diz por que — e dizendo o nome.
            funilDestino = escolhido;
        }
        else
        {
            var livres = await db.Pipelines.AsNoTracking()
                .Where(p => !ocupados.Contains(p.Id))
                .OrderBy(p => p.Ordem).ThenBy(p => p.Id)
                .Select(p => p.Id)
                .ToListAsync(ct);

            // Sem funil livre nao ha escolha possivel, e a recusa precisa dizer O QUE FAZER —
            // senao o vendedor fica clicando num botao que so devolve erro.
            if (livres.Count == 0)
                throw new RegraDeNegocioException(
                    "Esta pessoa já tem um negócio em todos os funis. "
                    + "Conclua o pedido ou encerre um deles antes de abrir outro.",
                    conflito: true);

            // ⚠️ POR `GanhaEm`, E NAO SO POR ID. Id nao e relogio neste projeto: a migracao
            // do elo (E4b) reinseriu as linhas vindas de `vendas`, e elas ficaram com ids
            // MAIORES que negociacoes mais novas. `GanhaEm` sobrevive a conclusao — concluir
            // mexe em `Status` e `ConcluidaEm`, nada mais — entao a data da compra continua
            // dizendo qual foi a ultima. O id fica como desempate.
            var daUltimaCompra = jaComprou
                ? await db.Negociacoes.AsNoTracking()
                    .Where(n => n.ContatoId == contato.Id)
                    .Where(n => n.Status == StatusNegociacao.Ganha
                             || n.Status == StatusNegociacao.Concluida)
                    .OrderByDescending(n => n.GanhaEm)
                    .ThenByDescending(n => n.Id)
                    .Select(n => (long?)n.PipelineId)
                    .FirstAsync(ct)
                : null;

            var padrao = await PipelinePadraoAsync(ct);

            funilDestino =
                perdida is { } morta && livres.Contains(morta.PipelineId) ? morta.PipelineId
                : daUltimaCompra is { } ultima && livres.Contains(ultima) ? ultima
                : livres.Contains(padrao) ? padrao
                : livres[0];

            // ⚠️ A PERDA SO E REVIVIDA NO PROPRIO FUNIL. Se ele estava ocupado, o negocio novo
            // nasce noutro lugar — e arrastar a perda para la apagaria a etapa onde ela morreu,
            // que e a unica informacao que reviver existe para preservar. Ela fica onde esta,
            // como historico, e o gesto vira uma linha nova.
            if (perdida is not null && perdida.PipelineId != funilDestino)
                perdida = null;
        }

        // ⚠️ `Aberta` OU `Ganha`: os dois APARECEM no quadro, e a regra e sobre CARDS. A ganha
        // fica na coluna Venda ate ser concluida — ate 7 dias, no padrao — e durante essa janela
        // abrir outra ali poria a mesma pessoa em duas etapas do mesmo funil.
        var jaNoFunil = await db.Negociacoes.AsNoTracking()
            .Where(n => n.ContatoId == contato.Id && n.PipelineId == funilDestino)
            .Where(n => n.Status == StatusNegociacao.Aberta || n.Status == StatusNegociacao.Ganha)
            .Select(n => (StatusNegociacao?)n.Status)
            .FirstOrDefaultAsync(ct);

        if (jaNoFunil is { } estado)
        {
            // O NOME DO FUNIL NA MENSAGEM, e nao so "ja esta em aberto": com varios funis, quem
            // le precisa saber em QUAL — senao a recusa parece arbitraria e a pessoa tenta de
            // novo no mesmo lugar.
            var nome = await db.Pipelines.AsNoTracking()
                .Where(x => x.Id == funilDestino).Select(x => x.Nome).FirstOrDefaultAsync(ct);

            // ⚠️ A MENSAGEM DA GANHA ENSINA A SAIDA. Recusar "porque ja tem uma venda" sem dizer
            // o que fazer manda o vendedor procurar suporte no meio de um atendimento. Concluir o
            // pedido e um clique na linha, e NAO tira o dinheiro do faturamento — concluida
            // continua contando, que e o que torna esta recusa aceitavel.
            throw new RegraDeNegocioException(
                estado == StatusNegociacao.Ganha
                    ? $"Este contato tem uma venda em {nome} aguardando conclusão. "
                    + "Conclua o pedido antes de abrir outro negócio nesse funil."
                    : $"Este contato já tem um negócio aberto em {nome}.",
                conflito: true);
        }

        if (perdida is not null)
        {
            // A ETAPA FICA: o negocio morreu ali, e retomar e continuar de onde parou.
            perdida.Status = StatusNegociacao.Aberta;
            perdida.PerdidaEm = null;
            perdida.MotivoPerda = null;

            trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Reabriu);
        }
        else
        {
            // ⚠️ SEMPRE NA PRIMEIRA ETAPA, nunca na de ganho. A ganha fica parada na coluna de
            // venda; nascer ali deixaria o card novo na coluna de ganho sem venda nenhuma — o
            // estado divergente que a porta unica do funil existe para impedir.
            //
            // A escolha do funil ja aconteceu la em cima (`funilDestino`), porque a checagem de
            // "ja ha aberto neste funil" precisa dela.
            var primeira = await PrimeiraEtapaAsync(funilDestino, ct);

            // ===================== DE ONDE VEIO ESTE NEGOCIO (NEG-3 -> E6) =====================
            // ⚠️ O CANAL PRECISOU MUDAR DE LUGAR. Ate o E6 o webhook gravava `canal_ciclo_id` na
            // negociacao no instante em que o lead nascia. Agora a negociacao nasce DEPOIS, aqui,
            // e sem esta leitura toda campanha se perderia: o relatorio "vendas por canal"
            // voltaria vazio para todo lead que chegou pela caixa — ou seja, para todos.
            //
            // A conversa guarda o canal do ciclo desde o INT-2, entao o dado nunca se perdeu; o
            // que mudou foi quem o copia. Mesma consulta de `MarcarGanhoAsync`, pelo mesmo motivo.
            // ==============================================================================
            var canalDoCiclo = await db.Conversas.AsNoTracking()
                .Where(c => c.ContatoId == contato.Id && c.CanalCicloId != null)
                .OrderByDescending(c => c.UltimaMensagemEm)
                .Select(c => c.CanalCicloId)
                .FirstOrDefaultAsync(ct);

            // SEM valor: o do negocio anterior era o preco daquela venda, e herda-lo poria um
            // numero na proposta nova que ninguem digitou.
            db.Negociacoes.Add(await AberturaDeNegociacao.NovaAsync(
                db, contato, primeira, await ProximaOrdemAsync(primeira, ct), null, canalDoCiclo, ct));

            trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Abriu);
        }

        await db.SaveChangesAsync(ct);

        // ===================== O ERP PRECISA SABER QUE O LEAD ENTROU (E6) =====================
        // ⚠️ AQUI NAO SE PUBLICAVA NADA, e ate o E6 isso passava despercebido: o lead entrava no
        // funil no instante em que nascia, e `lead.criado` ja saia com a etapa dentro.
        //
        // Agora `lead.criado` sai SEM etapa — o lead esta na caixa — e a entrada no funil e este
        // gesto, minutos ou dias depois. Sem esta linha, quem integra recebia o lead e nunca
        // ficava sabendo que ele virou negocio: so descobriria no proximo arrasto ou na venda.
        //
        // `lead.movido` com etapa anterior NULA e a descricao exata do que aconteceu — veio de
        // lugar nenhum para a primeira etapa.
        // ================================================================================
        await eventos.PublicarContatoAsync(EventoWebhook.LeadMovido, contato, ct: ct);
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
