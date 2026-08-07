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

        q = filtro switch
        {
            FiltroContato.Ganhos => q.Where(c => c.GanhoEm != null),
            FiltroContato.Perdidos => q.Where(c => c.PerdidoEm != null),
            FiltroContato.Todos => q,
            _ => q.Where(c => c.GanhoEm == null && c.PerdidoEm == null)
        };

        if (etapaId is { } e) q = q.Where(c => c.EtapaId == e);
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
                c.EtapaId, EtapaNome = c.Etapa.Nome, c.OrdemKanban,
                c.ResponsavelId, ResponsavelNome = c.Responsavel == null ? null : c.Responsavel.Nome,
                c.Valor, c.GanhoEm, c.PerdidoEm, c.CriadoEm,
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
            c.EtapaId, c.EtapaNome, c.OrdemKanban,
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
                x.EtapaId, EtapaNome = x.Etapa.Nome, x.OrdemKanban,
                x.ResponsavelId, ResponsavelNome = x.Responsavel == null ? null : x.Responsavel.Nome,
                x.Valor, x.GanhoEm, x.PerdidoEm, x.CriadoEm,
                x.OrigemDetalhe, x.Observacoes, x.MotivoPerda, x.AnonimizadoEm,
                Conversa = db.Conversas
                    .Where(v => v.ContatoId == x.Id)
                    .Select(v => new { v.Id, v.AguardandoDesde, v.NaoLidas, v.UltimaMensagemEm })
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

        return new ContatoDetalhe(
            resumo, c.OrigemDetalhe, c.Observacoes, c.MotivoPerda, c.AnonimizadoEm,
            c.Conversa?.UltimaMensagemEm, lembretes);
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
            : await PrimeiraEtapaAsync(ct);

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
            Valor = novo.Valor,
            Observacoes = Vazio(novo.Observacoes),
            // Entra no FIM da coluna. Lead novo no topo empurraria para baixo o que o vendedor já
            // estava trabalhando, e a ordem do quadro é dele, não do sistema.
            OrdemKanban = await ProximaOrdemAsync(etapaId, ct)
        };

        db.Contatos.Add(contato);
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
        contato.Valor = dados.Valor;
        contato.Observacoes = Vazio(dados.Observacoes);

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
    public async Task MarcarGanhoAsync(long id, decimal valor, CancellationToken ct)
    {
        if (valor <= 0)
            throw new RegraDeNegocioException("Informe o valor da venda.");

        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        if (contato.GanhoEm is not null)
            throw new RegraDeNegocioException("Esta venda já está marcada como fechada.", conflito: true);

        // ck_contatos_terminal proíbe ganho e perda juntos. Recusar aqui, com instrução, é melhor
        // que limpar a perda por baixo do pano: o vendedor perderia o registro de que houve uma
        // perda antes, e ninguém entenderia depois por que o histórico sumiu.
        if (contato.PerdidoEm is not null)
            throw new RegraDeNegocioException(
                "Este contato está marcado como perdido. Reabra antes de registrar a venda.",
                conflito: true);

        var agora = relogio.GetUtcNow().UtcDateTime;

        contato.Valor = valor;
        contato.GanhoEm = agora;

        // A SEGUNDA METADE DA PORTA ÚNICA: carimbar e mover na MESMA operação. É isto que permite
        // ao cliente tratar "arrastar para Venda" e "clicar em Venda fechada" como a mesma coisa.
        var etapaAnterior = contato.EtapaId;
        var etapaGanho = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.EGanho).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);

        if (etapaGanho is { } destino && destino != contato.EtapaId)
        {
            contato.EtapaId = destino;
            contato.OrdemKanban = await ProximaOrdemAsync(destino, ct);
        }

        // ===================== O CARIMBO E O HISTÓRICO, JUNTOS (NEG-1) =====================
        // A coluna diz em que estado o contato está AGORA; a linha registra o que ACONTECEU.
        // Reabrir limpa a coluna — e era aí que a venda anterior sumia, porque não havia linha.
        //
        // MESMA transação, e é o `SaveChanges` único abaixo que garante: os dois entram ou
        // nenhum entra. Gravar em duas chamadas deixaria a janela em que existe carimbo sem
        // faturamento (ou faturamento sem carimbo), e nenhum dos dois estados tem conserto
        // automático depois.
        //
        // `FechadaEm == GanhoEm` no mesmo instante NÃO é redundância: é a chave que liga o
        // carimbo à linha, e o que permite ao cancelamento saber se a venda é a vigente.
        //
        // `EtapaId` congela a etapa de ganho do momento — a empresa pode renomeá-la depois, e um
        // relatório do mês passado precisa dizer o que estava escrito lá.
        // ===================================================================================
        // A trilha (AUD-1): o contato passou a ganho. A venda ganha o evento dela DEPOIS do
        // save, quando o id existir — ver o comentário em `CriarAsync`.
        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Ganhou);

        var venda = new Venda
        {
            EmpresaId = contato.EmpresaId,
            ContatoId = contato.Id,
            Valor = valor,
            FechadaEm = agora,
            // ⚠️ 0 NÃO É USUÁRIO. Sem sessão (job, migração) o contexto traz zero, e gravá-lo
            // aqui viola `FK_vendas_usuarios_responsavel_id` — a venda inteira falha, e com ela o
            // fechamento. Encontrado pelo teste de ator=sistema do AUD-1.
            ResponsavelId = contexto.UsuarioId == 0 ? null : contexto.UsuarioId,
            EtapaId = etapaGanho ?? contato.EtapaId
        };
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

        if (diasParaConcluir == 0)
        {
            venda.Status = StatusVenda.Concluida;
            venda.ConcluidaEm = agora;
            venda.ConcluidaPor = null;
        }

        db.Vendas.Add(venda);

        await db.SaveChangesAsync(ct);

        trilha.Declarar(EntidadeAuditada.Venda, venda.Id, AcaoAuditoria.Criou,
            new Dictionary<string, AlteracaoValor> { ["valor"] = new(null, valor) });

        if (diasParaConcluir == 0)
            trilha.Declarar(EntidadeAuditada.Venda, venda.Id, AcaoAuditoria.Concluiu);
        await db.SaveChangesAsync(ct);

        // UM evento, não dois. Carimbar o ganho move de etapa junto, mas quem recebe `venda.fechada`
        // não precisa também de um `lead.movido` da mesma ação — seriam dois eventos para uma coisa
        // só, e o receptor teria que adivinhar que são o mesmo fato. A etapa anterior vai DENTRO
        // do payload de `venda.fechada`, que é onde ela é útil.
        await eventos.PublicarContatoAsync(EventoWebhook.VendaFechada, contato, etapaAnterior, ct);
    }

    public async Task MarcarPerdidoAsync(long id, string motivo, CancellationToken ct)
    {
        var texto = Exigir(motivo, "Informe o motivo da perda.");

        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        if (contato.PerdidoEm is not null)
            throw new RegraDeNegocioException("Este contato já está marcado como perdido.", conflito: true);

        if (contato.GanhoEm is not null)
            throw new RegraDeNegocioException(
                "Este contato está marcado como venda fechada. Reabra antes de marcar como perdido.",
                conflito: true);

        contato.PerdidoEm = relogio.GetUtcNow().UtcDateTime;
        contato.MotivoPerda = texto;
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

        if (contato.GanhoEm is null && contato.PerdidoEm is null)
            throw new RegraDeNegocioException("Este contato já está em aberto.", conflito: true);

        trilha.Declarar(EntidadeAuditada.Contato, contato.Id, AcaoAuditoria.Reabriu);

        contato.GanhoEm = null;
        contato.PerdidoEm = null;
        contato.MotivoPerda = null;
        // `valor` PERMANECE: é a estimativa do negócio, não o registro da venda, e apagá-lo
        // obrigaria o vendedor a digitar tudo de novo ao reabrir.

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
            var primeira = await PrimeiraEtapaAsync(ct);
            contato.EtapaId = primeira;
            contato.OrdemKanban = await ProximaOrdemAsync(primeira, ct);
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
    /// jsonb reconstruído chave a chave: as que não são PII (etapa, valor, ganho_em) permanecem
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

    private async Task<long> PrimeiraEtapaAsync(CancellationToken ct) =>
        await db.EtapasFunil.AsNoTracking()
            .OrderBy(e => e.Ordem).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException(
            "Esta empresa não tem funil configurado. Fale com o suporte.");

    /// <summary>A ordem do FIM da coluna. MAX no SQL, nunca varrendo a coluna em memória.</summary>
    private async Task<decimal> ProximaOrdemAsync(long etapaId, CancellationToken ct)
    {
        var ultima = await db.Contatos.AsNoTracking()
            .Where(c => c.EtapaId == etapaId && c.PerdidoEm == null)
            .MaxAsync(c => (decimal?)c.OrdemKanban, ct);

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
