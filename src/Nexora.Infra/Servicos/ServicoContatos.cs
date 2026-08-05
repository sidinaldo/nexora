using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
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

        contato.Valor = valor;
        contato.GanhoEm = relogio.GetUtcNow().UtcDateTime;

        // A SEGUNDA METADE DA PORTA ÚNICA: carimbar e mover na MESMA operação. É isto que permite
        // ao cliente tratar "arrastar para Venda" e "clicar em Venda fechada" como a mesma coisa.
        var etapaGanho = await db.EtapasFunil.AsNoTracking()
            .Where(e => e.EGanho).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct);

        if (etapaGanho is { } destino && destino != contato.EtapaId)
        {
            contato.EtapaId = destino;
            contato.OrdemKanban = await ProximaOrdemAsync(destino, ct);
        }

        await db.SaveChangesAsync(ct);
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
        // NÃO muda de etapa: ix_contatos_kanban filtra `perdido_em IS NULL`, então o card sai do
        // quadro sozinho — e preservar a etapa registra ONDE a negociação morreu.
        await db.SaveChangesAsync(ct);
    }

    public async Task ReabrirAsync(long id, CancellationToken ct)
    {
        var contato = await CarregarAsync(id, ct);
        RecusarSeAnonimizado(contato);

        if (contato.GanhoEm is null && contato.PerdidoEm is null)
            throw new RegraDeNegocioException("Este contato já está em aberto.", conflito: true);

        contato.GanhoEm = null;
        contato.PerdidoEm = null;
        contato.MotivoPerda = null;
        // `valor` PERMANECE: é a estimativa do negócio, não o registro da venda, e apagá-lo
        // obrigaria o vendedor a digitar tudo de novo ao reabrir.

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
    }

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
