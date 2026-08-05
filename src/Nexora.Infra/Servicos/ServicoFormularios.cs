using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;

namespace Nexora.Infra.Servicos;

/// <summary>Os formulários de captação, na área logada.
///
/// Aqui o query filter global VALE — é caminho autenticado, e `db.FormulariosCaptura` já sai
/// recortado pela empresa da requisição. O oposto do `ServicoCaptura`, que roda sem sessão e
/// resolve o tenant pela chave.
///
/// O enforcement de PAPEL (só dono) é do controller, como no resto do sistema.</summary>
public class ServicoFormularios(
    Persistencia.NexoraDbContext db,
    IContextoEmpresa contexto,
    TimeProvider relogio) : IServicoFormularios
{
    private const int MaximoPorEmpresa = 20;

    public async Task<IReadOnlyList<FormularioDto>> ListarAsync(CancellationToken ct) =>
        await db.FormulariosCaptura.AsNoTracking()
            .OrderByDescending(f => f.Ativo).ThenBy(f => f.Nome)
            .Select(f => new FormularioDto(
                f.Id, f.Nome, f.Chave, f.DominioPermitido, f.Ativo, f.LeadsRecebidos, f.CriadoEm))
            .ToListAsync(ct);

    public async Task<long> CriarAsync(NovoFormulario novo, CancellationToken ct)
    {
        var nome = ValidarNome(novo.Nome);
        var dominio = ValidarDominio(novo.DominioPermitido);

        // Teto por empresa: não é limite de produto, é freio contra script. Vinte formulários é
        // mais do que qualquer PME usa, e sem teto uma chamada em laço encheria a tabela.
        if (await db.FormulariosCaptura.CountAsync(ct) >= MaximoPorEmpresa)
            throw new RegraDeNegocioException(
                $"Limite de {MaximoPorEmpresa} formulários atingido. Desative ou reaproveite um existente.");

        if (await db.FormulariosCaptura.AnyAsync(f => f.Nome == nome, ct))
            throw new RegraDeNegocioException(
                "Já existe um formulário com este nome.", conflito: true);

        var formulario = new FormularioCaptura
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Chave = ServicoCaptura.GerarChave(),
            DominioPermitido = dominio,
            Ativo = true
        };

        db.FormulariosCaptura.Add(formulario);
        await db.SaveChangesAsync(ct);
        return formulario.Id;
    }

    public async Task AtualizarAsync(long id, NovoFormulario dados, CancellationToken ct)
    {
        var formulario = await MeuFormularioAsync(id, ct);
        var nome = ValidarNome(dados.Nome);

        if (await db.FormulariosCaptura.AnyAsync(f => f.Nome == nome && f.Id != id, ct))
            throw new RegraDeNegocioException(
                "Já existe um formulário com este nome.", conflito: true);

        // O nome vai para `contatos.origem_detalhe` de cada lead novo. Renomear NÃO reescreve os
        // que já vieram: eles registram de onde vieram no dia em que vieram, e reescrever isso
        // apagaria a história do lead para acertar um rótulo.
        formulario.Nome = nome;
        formulario.DominioPermitido = ValidarDominio(dados.DominioPermitido);
        await db.SaveChangesAsync(ct);
    }

    public async Task AlternarAtivoAsync(long id, bool ativo, CancellationToken ct)
    {
        var formulario = await MeuFormularioAsync(id, ct);
        formulario.Ativo = ativo;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> RegerarChaveAsync(long id, CancellationToken ct)
    {
        var formulario = await MeuFormularioAsync(id, ct);

        // A chave antiga para de funcionar NA HORA — é o ponto: quem regera está reagindo a um
        // vazamento. O HTML no site do cliente precisa ser trocado, e a tela avisa isso.
        formulario.Chave = ServicoCaptura.GerarChave();
        formulario.AtualizadoEm = relogio.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        return formulario.Chave;
    }

    /// <summary>O query filter já recorta por empresa; o `FirstOrDefault` nulo vira "não
    /// encontrado" — que é a resposta certa tanto para id inexistente quanto para id de outro
    /// tenant. Distinguir os dois contaria que o formulário existe em outra empresa.</summary>
    private async Task<FormularioCaptura> MeuFormularioAsync(long id, CancellationToken ct) =>
        await db.FormulariosCaptura.FirstOrDefaultAsync(f => f.Id == id, ct)
        ?? throw new RegraDeNegocioException("Formulário não encontrado.");

    private static string ValidarNome(string? nome)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length < 2)
            throw new RegraDeNegocioException("Dê um nome ao formulário (mínimo 2 caracteres).");
        return limpo.Length <= 80 ? limpo : limpo[..80];
    }

    /// <summary>Aceita `cliente.com.br` e também `https://cliente.com.br/contato` — guarda só o
    /// host. Exigir o formato exato transformaria uma proteção em chamado de suporte.</summary>
    private static string? ValidarDominio(string? dominio)
    {
        var limpo = (dominio ?? "").Trim();
        if (limpo.Length == 0) return null;

        if (Uri.TryCreate(limpo, UriKind.Absolute, out var uri)) return uri.Host;

        // Sem esquema o `Uri` não parseia; corta o que vier depois da primeira barra.
        var host = limpo.Split('/')[0];
        if (host.Contains(' ') || !host.Contains('.'))
            throw new RegraDeNegocioException(
                $"Domínio inválido: \"{limpo}\". Use algo como www.suaempresa.com.br.");

        return host.Length <= 200 ? host : host[..200];
    }
}
