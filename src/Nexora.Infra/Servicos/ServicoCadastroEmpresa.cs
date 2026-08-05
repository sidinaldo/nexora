using Microsoft.EntityFrameworkCore;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

public class ServicoCadastroEmpresa(NexoraDbContext db) : IServicoCadastroEmpresa
{
    private const int TamanhoMinimoSenha = 8;

    /// <summary>As 5 etapas fixas da fase 1. Funil configuravel e fase 2 — por isso a lista
    /// vive aqui e nao numa tabela de configuracao.</summary>
    private static readonly (string Nome, short Ordem, string Cor, bool EGanho)[] EtapasPadrao =
    [
        ("Novo Lead",            1, "#7FA88B", false),
        ("Primeiro Atendimento", 2, "#5C8F6E", false),
        ("Proposta",             3, "#3E7554", false),
        ("Negociação",           4, "#2F5D3A", false),
        ("Venda",                5, "#1E4028", true)
    ];

    public async Task<long> CadastrarAsync(NovaEmpresa nova, CancellationToken ct)
    {
        var nome = (nova.Nome ?? "").Trim();
        var nomeDono = (nova.NomeDono ?? "").Trim();
        var email = (nova.EmailDono ?? "").Trim();

        if (nome.Length == 0) throw new RegraDeNegocioException("Informe o nome da empresa.");
        if (nomeDono.Length == 0 || email.Length == 0)
            throw new RegraDeNegocioException("Informe nome e e-mail do responsável.");
        if ((nova.Senha ?? "").Length < TamanhoMinimoSenha)
            throw new RegraDeNegocioException($"A senha precisa de ao menos {TamanhoMinimoSenha} caracteres.");

        // SEM tenant no contexto — a empresa ainda nao existe. Sem IgnoreQueryFilters estas
        // checagens comparariam EmpresaId com 0, voltariam vazias, e o cadastro aceitaria
        // e-mail duplicado em silencio (estourando so no indice unico, com erro ilegivel).
        if (await db.Usuarios.IgnoreQueryFilters()
                .AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct))
            throw new RegraDeNegocioException("Já existe usuário com este e-mail.", conflito: true);

        var instanciaPedida = (nova.InstanceName ?? "").Trim();
        if (instanciaPedida.Length > 0
            && await db.Conexoes.IgnoreQueryFilters().AnyAsync(c => c.InstanceName == instanciaPedida, ct))
            throw new RegraDeNegocioException("Esta instância da Evolution já está em uso.", conflito: true);

        // Transacao propria so quando nao ha uma em curso: o chamador (teste, ou um fluxo
        // maior) pode ja ter aberto a dele, e abrir aninhada lanca.
        var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            var empresa = new Empresa
            {
                Nome = nome,
                Documento = string.IsNullOrWhiteSpace(nova.Documento)
                    ? null : new string([.. nova.Documento.Where(char.IsDigit)])
            };
            db.Empresas.Add(empresa);
            await db.SaveChangesAsync(ct);   // precisa do id para as filhas

            db.Usuarios.Add(new Usuario
            {
                EmpresaId = empresa.Id,
                Nome = nomeDono,
                Email = email,
                SenhaHash = HashSenha.Gerar(nova.Senha!),
                Papel = PapelUsuario.Dono,
                Status = StatusUsuario.Ativo
            });

            foreach (var (nomeEtapa, ordem, cor, eGanho) in EtapasPadrao)
                db.EtapasFunil.Add(new EtapaFunil
                {
                    EmpresaId = empresa.Id,
                    Nome = nomeEtapa,
                    Ordem = ordem,
                    Cor = cor,
                    EGanho = eGanho
                });

            db.Conexoes.Add(new Conexao
            {
                EmpresaId = empresa.Id,
                Nome = string.IsNullOrWhiteSpace(nova.NomeConexao) ? "Principal" : nova.NomeConexao!.Trim(),
                // Deterministico a partir do id: unico por construcao, e o cliente nao precisa
                // inventar um nome tecnico que so a Evolution entende.
                InstanceName = instanciaPedida.Length > 0 ? instanciaPedida : $"emp-{empresa.Id}",
                Status = StatusConexao.NaoCriada
            });

            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
            return empresa.Id;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }
}
