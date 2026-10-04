namespace Nexora.Core.Entidades;

/// <summary>===================== O QUE DIVERGE DO PAPEL, PARA UMA PESSOA (PER-1) =====================
///
/// Uma linha por EXCEÇÃO, não por permissão. O papel continua sendo a base — esta tabela guarda só
/// o que o dono ligou ou desligou para alguém especificamente.
///
/// ⚠️ EXCEÇÃO, E NÃO A LISTA PRONTA, e a diferença é o que faz a feature envelhecer bem:
///   · "contratei um vendedor" continua sendo um clique, sem montar dez marcações;
///   · uma permissão nova no produto alcança todo mundo sozinha, pela tabela em `Permissoes` —
///     com uma cópia congelada por usuário, ela não alcançaria ninguém;
///   · o diff de quem mudou de acesso é legível: são as linhas que existem.
///
/// ⚠️ `Permissao` É `text` COM O NOME DA API, contra a convenção de enum nativo do projeto. O
/// motivo: o enum do C# cresce a cada feature, e `ALTER TYPE ADD VALUE` por permissão nova é
/// custo recorrente — pior, remover uma permissão do código ficaria impossível sem migration das
/// linhas órfãs. Com `text`, a linha é legível no banco, é literalmente o que
/// `GET /auth/permissoes` devolve, e um nome desconhecido é ignorado na leitura
/// (`Permissoes.DoNomeDaApi` devolve `null`).
///
/// ⚠️ NEM TODO GESTO PODE ESTAR AQUI. `gerenciar_equipe` e `configurar_empresa` são indelegáveis, e
/// a recusa vale em TRÊS lugares: a tela não oferece, o `ServicoEquipe` não grava, e
/// `Permissoes.Pode` ignora. O terceiro é o que importa — uma linha forjada direto no banco não
/// vira acesso.
/// ============================================================================================</summary>
public class UsuarioPermissao : IEntidadeCriada
{
    public long EmpresaId { get; set; }
    public long UsuarioId { get; set; }

    /// <summary>O gesto, com o nome da API (`cancelar_venda`).</summary>
    public string Permissao { get; set; } = "";

    /// <summary>`true` concede o que o papel não dá; `false` revoga o que o papel dá.
    ///
    /// ⚠️ A COLUNA EXISTE PORQUE A EXCEÇÃO TEM DOIS SENTIDOS. Guardar só as concessões (a linha
    /// existe = pode) obrigaria a revogação a ser outra coisa — e aí "este gestor não vê os
    /// números da equipe", que é metade do pedido, não teria onde morar.</summary>
    public bool Concedida { get; set; }

    public DateTime CriadoEm { get; set; }

    /// <summary>Quem deu ou tirou. NULO quando não havia sessão — semente, migração, script.
    /// Mesmo idioma de `ContatoEtiqueta.CriadoPor`: `IContextoEmpresa.UsuarioId` devolve 0 fora de
    /// requisição autenticada, e gravar 0 criaria FK apontando para usuário inexistente.</summary>
    public long? CriadoPor { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Usuario Usuario { get; set; } = null!;
}
