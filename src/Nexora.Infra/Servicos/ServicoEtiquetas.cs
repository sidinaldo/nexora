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

    public async Task<IReadOnlyList<EtiquetaDto>> ListarAsync(CancellationToken ct) =>
        await db.Etiquetas.AsNoTracking()
            .OrderBy(e => e.Nome)
            .Select(e => new EtiquetaDto(e.Id, e.Nome, e.Cor))
            .ToListAsync(ct);

    // ==================================================================== criar
    public async Task<long> CriarAsync(NovaEtiqueta nova, CancellationToken ct)
    {
        var nome = ValidarNome(nova.Nome);

        var existentes = await db.Etiquetas.AsNoTracking()
            .Select(e => new { e.Id, e.Nome }).ToListAsync(ct);

        if (existentes.Count >= MaximoEtiquetas)
            throw new RegraDeNegocioException(
                $"A empresa já tem {MaximoEtiquetas} etiquetas. Apague alguma antes de criar outra.");

        ExigirNomeLivre(existentes.Select(e => (e.Id, e.Nome)), nome, ignorarId: null);

        var etiqueta = new Etiqueta
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Cor = ValidarCor(nova.Cor)
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

    // ==================================================================== validação
    private static string ValidarNome(string? nome)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length < TamanhoMinimoNome)
            throw new RegraDeNegocioException(
                $"Dê um nome à etiqueta (mínimo {TamanhoMinimoNome} caracteres).");
        return limpo.Length <= TamanhoMaximoNome ? limpo : limpo[..TamanhoMaximoNome];
    }

    /// <summary>===================== A CHECAGEM ACONTECE DUAS VEZES, E É DE PROPÓSITO =====================
    ///  Aqui, para devolver "Já existe uma etiqueta chamada X" — mensagem que o dono lê e entende.
    ///  E no banco, por `uq_etiquetas_nome`, que é quem de fato garante: entre esta consulta e o
    ///  `SaveChanges` cabe outra requisição criando o mesmo nome, e só o índice fecha essa janela.
    ///
    ///  Comparação SEM ACENTO não entra: "Ação" e "Acao" são nomes diferentes para quem lê, e
    ///  inventar equivalência aqui surpreenderia mais do que ajudaria. Mesma decisão do
    ///  `ServicoEtapas`.
    ///  ======================================================================================</summary>
    private static void ExigirNomeLivre(
        IEnumerable<(long Id, string Nome)> existentes, string nome, long? ignorarId)
    {
        if (existentes.Any(e => e.Id != ignorarId
                             && string.Equals(e.Nome, nome, StringComparison.OrdinalIgnoreCase)))
            throw new RegraDeNegocioException($"Já existe uma etiqueta chamada \"{nome}\".");
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
