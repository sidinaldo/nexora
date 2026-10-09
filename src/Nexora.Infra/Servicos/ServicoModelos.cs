using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.CloudApi;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>===================== OS TEMPLATES DA API OFICIAL (INT-XX) =====================
///
/// Roda DENTRO de requisicao autenticada: o query filter global vale, e template ou conexao de
/// outra empresa simplesmente nao e encontrado.
///
/// ⚠️ O TEMPLATE E DA CONEXAO QUE O CRIOU. A Meta o aprova para a conta (WABA) inteira, mas aqui ele
/// sai pelo token e pelas conversas da conexao dele. Dois numeros na mesma conta nao repetem nome:
/// a Meta recusaria, e a recusa vem antes, daqui.
///
/// So o RASCUNHO e editavel. Depois de enviado, o texto e o que a Meta revisou: mudar aqui faria a
/// thread mostrar uma coisa e o cliente receber outra.
/// ===================================================================================</summary>
public class ServicoModelos(
    NexoraDbContext db,
    IClienteCloudApi cloud,
    CifraSegredos cifra) : IServicoModelos
{
    private static readonly Regex FormatoIdioma = new("^[a-z]{2,3}(_[A-Z]{2})?$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<ModeloDto>> ListarAsync(long conexaoId, CancellationToken ct)
    {
        await ConexaoOficialAsync(conexaoId, ct);

        var modelos = await db.ModelosMensagem.AsNoTracking()
            .Where(m => m.ConexaoId == conexaoId)
            .OrderBy(m => m.Nome).ThenBy(m => m.Idioma)
            .ToListAsync(ct);

        return modelos.Select(Dto).ToList();
    }

    public async Task<long> CriarAsync(long conexaoId, NovoModelo novo, CancellationToken ct)
    {
        var conexao = await ConexaoOficialAsync(conexaoId, ct);

        var modelo = new ModeloMensagem
        {
            EmpresaId = conexao.EmpresaId,
            ConexaoId = conexao.Id,
            WabaId = conexao.WabaId!,
            Status = StatusModelo.Rascunho
        };
        Preencher(modelo, novo);
        await ExigirNomeLivreAsync(modelo, ct);

        db.ModelosMensagem.Add(modelo);
        await db.SaveChangesAsync(ct);
        return modelo.Id;
    }

    public async Task EditarAsync(long id, NovoModelo novo, CancellationToken ct)
    {
        var modelo = await ModeloAsync(id, ct);
        if (modelo.Status != StatusModelo.Rascunho)
            throw new RegraDeNegocioException(
                "Só o rascunho pode ser editado: depois de enviado, o texto é o que a Meta revisou. "
              + "Crie outro template.", conflito: true);

        Preencher(modelo, novo);
        await ExigirNomeLivreAsync(modelo, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ExcluirAsync(long id, CancellationToken ct)
    {
        var modelo = await ModeloAsync(id, ct);

        // Em revisao ou aprovado, ele existe na conta da Meta: apagar so daqui deixaria o Nexora sem
        // saber de um template que continua la.
        if (modelo.Status == StatusModelo.Enviado || modelo.Status == StatusModelo.Aprovado)
            throw new RegraDeNegocioException(
                "Template em revisão ou aprovado não se apaga por aqui: ele existe na conta da Meta.",
                conflito: true);

        if (await db.Mensagens.AnyAsync(m => m.ModeloId == id, ct))
            throw new RegraDeNegocioException(
                "Este template já foi enviado a clientes e fica no histórico das conversas.", conflito: true);

        db.ModelosMensagem.Remove(modelo);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ModeloDto> SubmeterAsync(long id, CancellationToken ct)
    {
        var modelo = await ModeloAsync(id, ct);
        if (modelo.Status != StatusModelo.Rascunho)
            throw new RegraDeNegocioException("Este template já foi enviado à Meta.", conflito: true);

        var resposta = await cloud.CriarModeloAsync(
            modelo.WabaId, Token(modelo.Conexao), modelo.Nome, modelo.Categoria.ToString().ToLowerInvariant(),
            modelo.Idioma, PreenchedorModelo.ParaMeta(modelo.Corpo, modelo.Variaveis),
            PreenchedorModelo.Exemplos(modelo.Variaveis), ct);

        modelo.IdMeta = resposta.Id;
        modelo.Status = StatusModelo.Enviado;
        RevisaoModelo.Aplicar(modelo, resposta.Status, resposta.MotivoRejeicao);

        await db.SaveChangesAsync(ct);
        return Dto(modelo);
    }

    public async Task<ModeloDto> SincronizarAsync(long id, CancellationToken ct)
    {
        var modelo = await ModeloAsync(id, ct);

        // Rascunho nao esta na Meta: nao ha o que perguntar.
        if (modelo.IdMeta == null) return Dto(modelo);

        var lido = await cloud.LerModeloAsync(modelo.IdMeta, Token(modelo.Conexao), ct);
        if (RevisaoModelo.Aplicar(modelo, lido.Status, lido.MotivoRejeicao))
            await db.SaveChangesAsync(ct);

        return Dto(modelo);
    }

    // ==================================================================== apoio
    private async Task<Conexao> ConexaoOficialAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await db.Conexoes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conexaoId, ct);
        if (conexao == null)
            throw new RegraDeNegocioException("Conexão não encontrada.") { StatusHttp = 404 };
        if (conexao.Canal != CanalWhatsapp.CloudApi)
            throw new RegraDeNegocioException(
                "Template é da API oficial. Pela conexão por QR code, texto livre sai a qualquer hora.");
        return conexao;
    }

    private async Task<ModeloMensagem> ModeloAsync(long id, CancellationToken ct)
    {
        var modelo = await db.ModelosMensagem.Include(m => m.Conexao).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (modelo == null)
            throw new RegraDeNegocioException("Template não encontrado.") { StatusHttp = 404 };
        return modelo;
    }

    private string Token(Conexao conexao) =>
        cifra.Decifrar(conexao.AccessTokenCifrado!, FinalidadeSegredo.AccessToken);

    private static void Preencher(ModeloMensagem modelo, NovoModelo novo)
    {
        var corpo = (novo.Corpo ?? "").Trim();

        modelo.Nome = PreenchedorModelo.NomeParaMeta(novo.Nome);
        modelo.Categoria = CategoriaDe(novo.Categoria);
        modelo.Idioma = IdiomaDe(novo.Idioma);
        modelo.Variaveis = PreenchedorModelo.VariaveisDe(corpo).ToArray();
        modelo.Corpo = corpo;
    }

    private async Task ExigirNomeLivreAsync(ModeloMensagem modelo, CancellationToken ct)
    {
        var repetido = await db.ModelosMensagem.AnyAsync(m =>
            m.Id != modelo.Id && m.WabaId == modelo.WabaId && m.Nome == modelo.Nome && m.Idioma == modelo.Idioma, ct);

        if (repetido)
            throw new RegraDeNegocioException(
                $"Já existe um template \"{modelo.Nome}\" em {modelo.Idioma} nesta conta da Meta.", conflito: true);
    }

    private static CategoriaModelo CategoriaDe(string? categoria)
    {
        var c = (categoria ?? "").Trim().ToLowerInvariant();
        if (c == "utility") return CategoriaModelo.Utility;
        if (c == "marketing") return CategoriaModelo.Marketing;
        if (c == "authentication")
            throw new RegraDeNegocioException(
                "Template de autenticação tem o texto fixo da Meta (o código de acesso) e não é criado por aqui.");
        throw new RegraDeNegocioException("Escolha a categoria: utilidade ou marketing.");
    }

    private static string IdiomaDe(string? idioma)
    {
        var i = (idioma ?? "").Trim();
        if (i.Length == 0) return "pt_BR";
        if (!FormatoIdioma.IsMatch(i))
            throw new RegraDeNegocioException("Idioma inválido. Use o código da Meta, como pt_BR.");
        return i;
    }

    internal static ModeloDto Dto(ModeloMensagem m) => new(
        m.Id, m.ConexaoId, m.Nome, m.Categoria.ToString().ToLowerInvariant(), m.Idioma, m.Corpo,
        m.Variaveis, m.Status.ToString().ToLowerInvariant(), m.MotivoRejeicao, m.AtualizadoEm);
}
