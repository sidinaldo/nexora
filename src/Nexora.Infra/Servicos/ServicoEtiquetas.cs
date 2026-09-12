using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O vocabulário de etiquetas: criar, renomear, trocar a cor, apagar.
///
/// Espelha `ServicoEtapas`, que é a lista por empresa com nome e cor mais próxima disto — mas sem
/// as quatro invariantes de lá. Etiqueta não tem ordem, não tem marca de ganho, e apagar uma não
/// deixa contato nenhum órfão. O que sobra é o nome único e a cor validada.</summary>
public class ServicoEtiquetas(NexoraDbContext db, IContextoEmpresa contexto) : IServicoEtiquetas
{
    /// <summary>Teto de etiquetas. Não é limite técnico: é que uma lista de cem rótulos deixa de
    /// ser vocabulário e vira busca — e ninguém acha "Urgente" rolando cem chips na hora de
    /// atender. O mesmo raciocínio do `MaximoEtapas` do funil, com folga maior porque etiqueta
    /// não ocupa coluna na tela.</summary>
    public const int MaximoEtiquetas = 60;

    private const int TamanhoMinimoNome = 2;
    private const int TamanhoMaximoNome = 30;

    public async Task<IReadOnlyList<EtiquetaNaLista>> ListarAsync(
        string? busca, OrdemEtiqueta ordem, CancellationToken ct)
    {
        var q = db.Etiquetas.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(busca))
        {
            // `ToLower()` dos DOIS lados, e não `Contains(..., OrdinalIgnoreCase)`: o EF traduz
            // este para `lower(nome) LIKE lower(...)` e o outro não traduz — viraria filtro em
            // memória depois de trazer a tabela inteira.
            //
            // A insensibilidade aqui não é conveniência: `uq_etiquetas_nome` já impede "VIP" e
            // "vip" coexistirem, então procurar "vip" PRECISA achar a "VIP" que sobreviveu.
            var texto = busca.Trim().ToLower();
            q = q.Where(e => e.Nome.ToLower().Contains(texto));
        }

        // ⚠️ O DESEMPATE POR `Id` NÃO É ENFEITE. Etiquetas criadas na mesma transação recebem o
        // MESMO `criado_em` — o `InterceptorAuditoria` carimba um instante só por `SaveChanges`.
        // Sem o desempate, "Recentes" devolveria ordem arbitrária do Postgres e o teste passaria
        // ou não conforme o plano de execução do dia.
        q = ordem switch
        {
            OrdemEtiqueta.Recentes => q.OrderByDescending(e => e.CriadoEm).ThenByDescending(e => e.Id),

            // ⚠️ DESEMPATA POR NOME, não por id. Numa lista de sessenta etiquetas, a maioria
            // empata em zero uso — e sem o desempate elas sairiam na ordem física do Postgres,
            // que muda sozinha. Quem ordena por uso ainda precisa achar "Urgente" no meio das
            // não usadas.
            OrdemEtiqueta.Uso => q
                .OrderByDescending(e => db.ContatosEtiquetas.Count(x => x.EtiquetaId == e.Id))
                .ThenBy(e => e.Nome),

            _ => q.OrderBy(e => e.Nome).ThenBy(e => e.Id)
        };

        return await q
            .Select(e => new EtiquetaNaLista(
                e.Id, e.Nome, e.Cor,
                // Subconsulta agregada contra `ix_contatos_etiquetas_etiqueta`, não a lista de
                // marcações materializada. É a mesma forma que `ServicoFunil` usa para
                // `VendasEmAberto` por card.
                db.ContatosEtiquetas.Count(x => x.EtiquetaId == e.Id)))
            .ToListAsync(ct);
    }

    // ==================================================================== criar
    public async Task<long> CriarAsync(NovaEtiqueta nova, CancellationToken ct)
    {
        var nome = ValidarNome(nova.Nome);

        var existentes = await db.Etiquetas.AsNoTracking()
            .Select(e => new { e.Id, e.Nome }).ToListAsync(ct);

        // 422, e não 400 nem 409: a entrada está perfeita e nenhuma outra etiqueta disputa este
        // nome. O que impede é um teto — a requisição é compreensível e bem formada, e ainda
        // assim não pode ser processada.
        if (existentes.Count >= MaximoEtiquetas)
            throw new RegraDeNegocioException(
                $"A empresa já tem {MaximoEtiquetas} etiquetas. Apague alguma antes de criar outra.")
            {
                StatusHttp = 422
            };

        ExigirNomeLivre(existentes.Select(e => (e.Id, e.Nome)), nome, ignorarId: null);

        var etiqueta = new Etiqueta
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Cor = ValidarCor(nova.Cor),

            // `== 0` é "não há sessão" (semente, migração, script), não "usuário zero". Gravar 0
            // criaria FK apontando para usuário inexistente. Mesmo idioma de `ServicoLembretes`.
            CriadoPor = contexto.UsuarioId == 0 ? null : contexto.UsuarioId
        };

        db.Etiquetas.Add(etiqueta);
        await db.SaveChangesAsync(ct);
        return etiqueta.Id;
    }

    // ==================================================================== atualizar
    public async Task AtualizarAsync(long id, EditarEtiqueta dados, CancellationToken ct)
    {
        var etiqueta = await db.Etiquetas.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new RegraDeNegocioException("Etiqueta não encontrada.");

        var nome = ValidarNome(dados.Nome);

        var existentes = await db.Etiquetas.AsNoTracking()
            .Select(e => new { e.Id, e.Nome }).ToListAsync(ct);

        ExigirNomeLivre(existentes.Select(e => (e.Id, e.Nome)), nome, ignorarId: id);

        etiqueta.Nome = nome;
        etiqueta.Cor = ValidarCor(dados.Cor);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== remover
    public async Task RemoverAsync(long id, CancellationToken ct)
    {
        var etiqueta = await db.Etiquetas.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new RegraDeNegocioException("Etiqueta não encontrada.");

        db.Etiquetas.Remove(etiqueta);
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== aplicar
    /// <summary>Teto de etiquetas POR CONTATO — diferente do teto de 60 do vocabulário.
    ///
    /// Os dois respondem perguntas diferentes: 60 é quanto vocabulário a empresa consegue manter
    /// coerente; 8 é quanto cabe num card sem ele deixar de informar. Um contato com quarenta
    /// etiquetas não tem quarenta informações — tem nenhuma, porque ninguém lê a lista.</summary>
    public const int MaximoPorContato = 8;

    public async Task<IReadOnlyList<EtiquetaDto>> DoContatoAsync(long contatoId, CancellationToken ct) =>
        await db.ContatosEtiquetas.AsNoTracking()
            .Where(x => x.ContatoId == contatoId)
            .OrderBy(x => x.Etiqueta.Nome)
            .Select(x => new EtiquetaDto(x.Etiqueta.Id, x.Etiqueta.Nome, x.Etiqueta.Cor))
            .ToListAsync(ct);

    public async Task AplicarAsync(
        long contatoId, IReadOnlyList<long> etiquetaIds, CancellationToken ct)
    {
        // Repetido na mesma requisição não é erro do usuário — é a tela mandando o que tinha na
        // mão. Deduplicar é mais gentil que recusar, e o resultado é o mesmo.
        var pedidas = (etiquetaIds ?? []).Distinct().ToList();

        if (pedidas.Count > MaximoPorContato)
            throw new RegraDeNegocioException(
                $"Um contato aceita no máximo {MaximoPorContato} etiquetas.");

        // O filtro de tenant já recorta: contato de outra empresa simplesmente não aparece, e a
        // mensagem é a mesma de "não existe" — que é a verdade do ponto de vista de quem pergunta.
        var contato = await db.Contatos.AsNoTracking()
            .Where(c => c.Id == contatoId).Select(c => (long?)c.Id).FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Contato não encontrado.");

        // ⚠️ VALIDA AS ETIQUETAS ANTES DE ESCREVER, mesmo com a FK composta cobrindo o caso. A FK
        // devolveria uma violação crua de banco, que vira 500; aqui vira mensagem legível. É a
        // mesma divisão de trabalho do nome único: o serviço explica, o banco garante.
        if (pedidas.Count > 0)
        {
            var existentes = await db.Etiquetas.AsNoTracking()
                .Where(e => pedidas.Contains(e.Id)).CountAsync(ct);

            if (existentes != pedidas.Count)
                throw new RegraDeNegocioException("Alguma das etiquetas não existe mais.");
        }

        var atuais = await db.ContatosEtiquetas
            .Where(x => x.ContatoId == contatoId).ToListAsync(ct);

        // ===================== SÓ O DELTA =====================
        // Apagar tudo e reinserir seria mais curto e perderia `criado_em` de quem já estava lá —
        // e "desde quando este cliente é VIP?" deixaria de ter resposta a cada vez que alguém
        // mexesse em qualquer outra etiqueta do mesmo contato.
        // ======================================================
        foreach (var sobrando in atuais.Where(x => !pedidas.Contains(x.EtiquetaId)))
            db.ContatosEtiquetas.Remove(sobrando);

        foreach (var nova in pedidas.Where(id => atuais.All(x => x.EtiquetaId != id)))
            db.ContatosEtiquetas.Add(new ContatoEtiqueta
            {
                EmpresaId = contexto.EmpresaId,
                ContatoId = contato,
                EtiquetaId = nova,
                // `== 0` é "não há sessão", não "usuário zero" — gravar 0 criaria FK quebrada.
                CriadoPor = contexto.UsuarioId == 0 ? null : contexto.UsuarioId
            });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>⚠️ A MESMA CONTA DE `ListarAsync`, e isso é obrigatório, não coincidência.
    ///
    /// O número da lista e o número da confirmação são lidos com segundos de diferença pela mesma
    /// pessoa. Se divergirem — "Urgente · 132 contatos" e logo em seguida "remover de 87" —, o
    /// dono não conclui "houve uma mudança": conclui que o sistema não sabe o que está dizendo.
    ///
    /// `A_CONTAGEM_DA_LISTA_BATE_COM_O_IMPACTO` é o teste que segura as duas juntas.</summary>
    public async Task<int> ImpactoAsync(long id, CancellationToken ct)
    {
        _ = await db.Etiquetas.AsNoTracking()
            .Where(e => e.Id == id).Select(e => (long?)e.Id).FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException("Etiqueta não encontrada.");

        return await db.ContatosEtiquetas.CountAsync(x => x.EtiquetaId == id, ct);
    }

    // ==================================================================== validação
    /// <summary>⚠️ NOME LONGO DEMAIS É RECUSADO, NÃO CORTADO.
    ///
    /// Até o DES-XX isto truncava em silêncio: mandar 40 caracteres gravava 30 e devolvia sucesso.
    /// O dono via a etiqueta aparecer com o nome pela metade sem nada explicar, e a tela — que
    /// limita o campo em `maxlength="30"` — nunca reproduzia o efeito. Só a API fazia, e era ela
    /// que o seletor de etiquetas e qualquer integração futura iam usar.
    ///
    /// Cortar dado do usuário sem avisar é pior que recusar: recusar ele conserta, truncar ele
    /// descobre depois.</summary>
    private static string ValidarNome(string? nome)
    {
        var limpo = (nome ?? "").Trim();

        if (limpo.Length < TamanhoMinimoNome)
            throw new RegraDeNegocioException(
                $"Dê um nome à etiqueta (mínimo {TamanhoMinimoNome} caracteres).");

        if (limpo.Length > TamanhoMaximoNome)
            throw new RegraDeNegocioException(
                $"O nome da etiqueta tem no máximo {TamanhoMaximoNome} caracteres.");

        return limpo;
    }

    /// <summary>===================== A CHECAGEM ACONTECE DUAS VEZES, E É DE PROPÓSITO =====================
    ///  Aqui, para devolver "Já existe uma etiqueta chamada X" — mensagem que o dono lê e entende.
    ///  E no banco, por `uq_etiquetas_nome`, que é quem de fato garante: entre esta consulta e o
    ///  `SaveChanges` cabe outra requisição criando o mesmo nome, e só o índice fecha essa janela.
    ///
    ///  Comparação SEM ACENTO não entra: "Ação" e "Acao" são nomes diferentes para quem lê, e
    ///  inventar equivalência aqui surpreenderia mais do que ajudaria. Mesma decisão do
    ///  `ServicoEtapas`.
    ///
    ///  `conflito: true` ⇒ 409, e não 400: o nome que chegou é válido em si. O que impede é o
    ///  ESTADO — já existe outra linha ocupando ele. É a mesma distinção que "Já existe um contato
    ///  com este telefone" faz em `ServicoContatos`.
    ///  ======================================================================================</summary>
    private static void ExigirNomeLivre(
        IEnumerable<(long Id, string Nome)> existentes, string nome, long? ignorarId)
    {
        if (existentes.Any(e => e.Id != ignorarId
                             && string.Equals(e.Nome, nome, StringComparison.OrdinalIgnoreCase)))
            throw new RegraDeNegocioException(
                $"Já existe uma etiqueta chamada \"{nome}\".", conflito: true);
    }

    /// <summary>Só hexadecimal de 6 dígitos. A cor vai direto para o `style` do chip: aceitar
    /// texto livre aqui seria deixar o dono escrever CSS na tela de todo mundo da empresa dele.
    /// Mesma validação do `ServicoEtapas`, pela mesma razão.</summary>
    private static string ValidarCor(string? cor)
    {
        var limpo = (cor ?? "").Trim();
        if (limpo.Length == 0) return "#2F5D3A";

        if (limpo.Length != 7 || limpo[0] != '#' || !limpo[1..].All(Uri.IsHexDigit))
            throw new RegraDeNegocioException(
                $"Cor inválida: \"{limpo}\". Use o formato #RRGGBB.");

        return limpo.ToUpperInvariant();
    }
}
