using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Os feriados em que a empresa não atende.
///
/// Globais (empresa_id NULL) são os nacionais, semeados pelo job anual a partir da
/// CalculadoraFeriados (função pura). Manuais são do tenant. E há um terceiro estado: global
/// DISPENSADO pela empresa — ver FeriadoIgnorado.</summary>
public class ServicoFeriados(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    TimeProvider relogio,
    ILogger<ServicoFeriados> log) : IServicoFeriados
{
    public async Task GarantirAtualEProximoAsync(CancellationToken ct)
    {
        var ano = relogio.GetUtcNow().Year;
        await SemearGlobaisAsync(ano, ct);
        // O próximo ano junto: sem isso, um follow-up agendado em 28/dez para depois do Natal
        // não teria calendário para consultar, e a virada de ano viraria um bug sazonal.
        await SemearGlobaisAsync(ano + 1, ct);

        await SemearEstaduaisAsync(ano, ct);
        await SemearEstaduaisAsync(ano + 1, ct);
    }

    /// <summary>Semeia os feriados ESTADUAIS das UFs que as empresas de fato usam.
    ///
    /// ===================== POR QUE PELAS UFs EM USO =====================
    /// Semear as 27 UFs de uma vez encheria a tabela com feriados que ninguém consulta, e todos
    /// eles são GLOBAIS (empresa_id NULL) — apareceriam na tela de configuração de toda empresa,
    /// inclusive a de outro estado. Semear pelo `DISTINCT uf` das empresas mantém a tabela do
    /// tamanho da realidade.
    ///
    /// Roda como JOB (sem tenant), então `IgnoreQueryFilters` + leitura explícita — a armadilha
    /// nº 1 do inventário: sem isso o `EmpresaId` é zero e a lista volta vazia em silêncio.
    /// ====================================================================
    ///
    /// UF SEM CADASTRO NÃO FALHA. `CalculadoraFeriados.Estaduais` devolve lista vazia, o laço não
    /// executa nenhuma vez e um log registra a lacuna. Lançar aqui derrubaria o seed inteiro por
    /// causa de um dado que falta — e o efeito seria não semear nem os nacionais.</summary>
    private async Task SemearEstaduaisAsync(int ano, CancellationToken ct)
    {
        var ufs = await db.Empresas.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Uf != null)
            .Select(e => e.Uf!)
            .Distinct()
            .ToListAsync(ct);

        foreach (var uf in ufs)
        {
            var feriados = CalculadoraFeriados.Estaduais(ano, uf);

            if (feriados.Count == 0)
            {
                log.LogInformation(
                    "Sem feriados estaduais cadastrados para {Uf} em {Ano}; a empresa recebe só " +
                    "os nacionais. Cadastro em CalculadoraFeriados.Estaduais.", uf, ano);
                continue;
            }

            foreach (var (data, nome) in feriados)
            {
                // Mesmo ON CONFLICT DO NOTHING dos nacionais. `uq_feriados` inclui
                // COALESCE(uf,''), então o estadual de RN não colide com o nacional da mesma
                // data — e reexecutar (boot + rodada diária) é no-op.
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO feriados (empresa_id, data, nome, abrangencia, uf, criado_em) " +
                    "VALUES (NULL, {0}, {1}, 'estadual'::abrangencia_feriado_enum, {2}, now()) " +
                    "ON CONFLICT DO NOTHING",
                    [data, nome, uf], ct);
            }
        }
    }

    /// <summary>Insere os feriados NACIONAIS do ano que ainda não existem.
    ///
    /// INSERT ... ON CONFLICT DO NOTHING contra uq_feriados, em vez de AddRange + SaveChanges:
    /// duas execuções concorrentes (boot + rodada diária, ou duas instâncias) não estouram — a
    /// corrida vira no-op. A abrangência é enum CONTROLADO (nunca entrada do usuário), então
    /// interpolar o literal é seguro; data e nome vão como parâmetros.
    ///
    /// ESTADUAIS não são semeados: `empresas` não tem coluna `uf`, então não há como saber a
    /// qual estado a empresa pertence. A estrutura (coluna `uf`, CalculadoraFeriados.Estaduais)
    /// está pronta para quando a UF entrar no cadastro.</summary>
    private async Task SemearGlobaisAsync(int ano, CancellationToken ct)
    {
        foreach (var (data, nome) in CalculadoraFeriados.Nacionais(ano))
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO feriados (empresa_id, data, nome, abrangencia, uf, criado_em) " +
                "VALUES (NULL, {0}, {1}, 'nacional'::abrangencia_feriado_enum, NULL, now()) " +
                "ON CONFLICT DO NOTHING",
                [data, nome], ct);
        }
    }

    public async Task<IReadOnlyList<FeriadoDto>> ProximosAsync(CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(relogio.GetUtcNow().UtcDateTime);

        // Os dispensados vêm MARCADOS, não filtrados: a tela precisa mostrá-los apagados, com a
        // opção de reativar. Sumir com eles esconderia do dono a decisão que ele mesmo tomou.
        var ignorados = await db.FeriadosIgnorados.AsNoTracking()
            .Select(x => x.FeriadoId).ToListAsync(ct);
        var conjunto = ignorados.ToHashSet();

        // O query filter já admite os globais e isola os manuais por tenant.
        var lista = await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= hoje)
            .OrderBy(f => f.Data)
            .Select(f => new
            {
                f.Id, f.Data, f.Nome,
                Abrangencia = f.Abrangencia.ToString().ToLower(),
                EhManual = f.EmpresaId != null
            })
            .ToListAsync(ct);

        return lista
            .Select(f => new FeriadoDto(
                f.Id, f.Data, f.Nome, f.Abrangencia, f.EhManual, conjunto.Contains(f.Id)))
            .ToList();
    }

    public async Task<long> CriarManualAsync(NovoFeriado novo, CancellationToken ct)
    {
        var nome = (novo.Nome ?? "").Trim();
        if (nome.Length == 0) throw new RegraDeNegocioException("Informe o nome do feriado.");

        if (novo.Data < DateOnly.FromDateTime(relogio.GetUtcNow().UtcDateTime))
            throw new RegraDeNegocioException("A data não pode estar no passado.");

        // Duplicata na MESMA DATA — inclusive contra um global. Deixar a empresa cadastrar
        // "Natal" em 25/12 por cima do nacional criaria dois feriados no mesmo dia, e o motor
        // consultaria os dois para nada.
        var jaExiste = await db.Feriados.AnyAsync(
            f => f.Data == novo.Data
              && (f.EmpresaId == contexto.EmpresaId || f.EmpresaId == null), ct);

        if (jaExiste)
            throw new RegraDeNegocioException("Já existe um feriado nessa data.", conflito: true);

        var feriado = new Feriado
        {
            EmpresaId = contexto.EmpresaId,
            Data = novo.Data,
            Nome = nome,
            Abrangencia = AbrangenciaFeriado.Manual,
            CriadoEm = relogio.GetUtcNow().UtcDateTime
        };
        db.Feriados.Add(feriado);
        await db.SaveChangesAsync(ct);
        return feriado.Id;
    }

    public async Task RemoverManualAsync(long id, CancellationToken ct)
    {
        // Só o próprio manual do tenant — NUNCA um global. O query filter admite globais, então
        // o filtro explícito aqui é o que impede uma empresa de apagar o Natal de todas.
        var feriado = await db.Feriados.FirstOrDefaultAsync(
            f => f.Id == id && f.EmpresaId == contexto.EmpresaId
              && f.Abrangencia == AbrangenciaFeriado.Manual, ct);

        if (feriado is null)
        {
            // Distingue "não existe" de "existe mas é nacional": a segunda mensagem ensina o
            // caminho certo em vez de deixar o dono achando que a tela está quebrada.
            var ehGlobal = await db.Feriados.AnyAsync(f => f.Id == id && f.EmpresaId == null, ct);
            throw ehGlobal
                ? new RegraDeNegocioException(
                    "Feriado nacional não pode ser apagado. Se a empresa atende nesse dia, " +
                    "marque-o como dia de trabalho.", conflito: true)
                : new RegraDeNegocioException("Feriado não encontrado.");
        }

        db.Feriados.Remove(feriado);
        await db.SaveChangesAsync(ct);
    }

    public async Task IgnorarAsync(long feriadoId, CancellationToken ct)
    {
        var global = await db.Feriados.AsNoTracking()
            .AnyAsync(f => f.Id == feriadoId && f.EmpresaId == null, ct);

        if (!global)
            throw new RegraDeNegocioException(
                "Só feriado nacional pode ser marcado como dia de trabalho. " +
                "Feriado criado pela empresa se apaga.", conflito: true);

        if (await db.FeriadosIgnorados.AnyAsync(x => x.FeriadoId == feriadoId, ct))
            return;   // idempotente: pedir duas vezes não é erro

        db.FeriadosIgnorados.Add(new FeriadoIgnorado
        {
            EmpresaId = contexto.EmpresaId,
            FeriadoId = feriadoId,
            CriadoEm = relogio.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task ReativarAsync(long feriadoId, CancellationToken ct)
    {
        var linha = await db.FeriadosIgnorados.FirstOrDefaultAsync(x => x.FeriadoId == feriadoId, ct);
        if (linha is null) return;   // idempotente

        db.FeriadosIgnorados.Remove(linha);
        await db.SaveChangesAsync(ct);
    }
}
