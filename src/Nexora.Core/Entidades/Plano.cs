namespace Nexora.Core.Entidades;

/// <summary>Um plano do catálogo comercial: nome, preço combinado e os tetos que ele dá.
///
/// ===================== O PLANO É UM MOLDE, NÃO UM PONTEIRO VIVO =====================
/// Atribuir um plano a uma empresa COPIA os limites para a linha dela. Editar o plano depois NÃO
/// muda empresa nenhuma — e isso é a decisão, não efeito colateral: limite é CONTRATO, e contrato
/// de quem assinou em março não muda porque a tabela de preços mudou em agosto.
///
/// Consequência de desenho: `empresas.plano_id` é RÓTULO. Quem manda são `empresas.limite_conexoes`
/// e `empresas.limite_usuarios`. É por isso que o ponto de validação (`ServicoConexoes.CriarAsync`)
/// não mudou uma linha quando esta tabela nasceu — exatamente como o comentário de
/// `Empresa.LimiteConexoes` previu que seria.
///
/// E é o que torna barato o caso mais comum do comercial: "esse cliente negociou uma conexão a
/// mais". Ajusta-se a empresa, sem tirá-la do plano e sem criar um plano de uma linha só.
/// ====================================================================================
///
/// ===================== A ÚNICA TABELA SEM FILTRO DE TENANT =====================
/// `planos` não tem `empresa_id` e não tem `HasQueryFilter`. É catálogo: o mesmo para todo mundo, e
/// só o operador escreve nele. Uma sessão de cliente que consultasse `db.Planos` veria a lista
/// inteira — e isso é aceitável, porque nome e preço de plano é o que uma página de vendas publica.
///
/// ⚠️ O QUE TORNARIA ISTO UM VAZAMENTO: uma coluna POR CLIENTE aqui. Desconto negociado, número de
/// contrato, data de renovação, observação do comercial — qualquer um deles transforma uma tabela
/// global inofensiva em leitura cruzada de tenant, sem nenhum erro e sem nenhum teste reprovando.
///
/// Dado por cliente mora em `empresas`, que TEM filtro. Esta tabela é molde, e molde não tem dono.
/// ==============================================================================</summary>
public class Plano : IEntidadeAuditada
{
    public long Id { get; set; }

    /// <summary>Único por `lower(nome)`. "Pro" e "pro" são o mesmo plano para quem lê a lista, e
    /// dois deles tornam a tela indecifrável no dia em que alguém precisar saber em qual plano a
    /// empresa está.</summary>
    public string Nome { get; set; } = null!;

    /// <summary>⚠️ ESTE CAMPO NÃO COBRA NADA. Não existe cobrança neste sistema — nem assinatura,
    /// nem gateway, nem fatura. É o registro do que foi combinado, lido por uma pessoa.
    ///
    /// Está dito aqui e na tela porque a leitura natural de um campo de preço ao lado de um botão
    /// "atribuir plano" é que mudá-lo muda o que o cliente paga. Não muda. Muda um rótulo.</summary>
    public decimal Preco { get; set; }

    /// <summary>Moeda não é coluna, e a constante existe para a suposição ser ACHÁVEL. O produto é
    /// Brasil de ponta a ponta — CNPJ, `uf char(2)`, fuso de São Paulo, feriados brasileiros —, e
    /// uma coluna com um único valor possível é coluna que ninguém lê. No dia em que houver a
    /// segunda moeda, isto vira migration em vez de arqueologia.</summary>
    public const string Moeda = "BRL";

    public short LimiteConexoes { get; set; } = 1;
    public short LimiteUsuarios { get; set; } = 3;

    /// <summary>Arquivar em vez de apagar. Não há delete físico neste schema (todas as FKs são
    /// RESTRICT), e aqui há um motivo a mais: `empresas.plano_id` é o único traço do que foi
    /// vendido. Uma empresa cujo plano sumiu fica com limites sem explicação.
    ///
    /// Isto cobre o "criei errado": o plano some da lista de atribuíveis e continua existindo para
    /// quem já está nele. Uma regra em vez de duas ("apaga se não usado, arquiva se usado").</summary>
    public bool Ativo { get; set; } = true;

    /// <summary>Ordem de exibição. Preço não serve: o catálogo é lido do mais barato ao mais caro,
    /// e um plano promocional caro-porém-primeiro quebraria isso.</summary>
    public short Ordem { get; set; } = 1;

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }
}
