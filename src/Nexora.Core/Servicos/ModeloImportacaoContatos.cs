using Nexora.Core.Csv;

namespace Nexora.Core.Servicos;

/// <summary>===================== O MODELO VIVE AO LADO DO IMPORTADOR =====================
///
/// A planilha que o dono baixa para preencher. Ela não é um arquivo de exemplo solto: é o
/// CONTRATO do importador escrito por extenso — os nomes de coluna aqui são os mesmos que
/// `ServicoImportacao` procura no cabeçalho.
///
/// ⚠️ POR ISSO ELA NASCE NO SERVIDOR, e não no navegador com o `baixarCsv` do painel. Gerada lá,
/// seria uma SEGUNDA declaração das colunas aceitas, a um `git pull` de distância de divergir da
/// primeira — e o sintoma seria o pior possível: o dono preenche 800 linhas no modelo que o
/// próprio produto deu e leva "o arquivo precisa da coluna telefone".
///
/// Aqui, um teste alimenta ESTES BYTES direto no `PreverAsync` e exige que passem. O arquivo que o
/// cliente baixa é o mesmo que o teste aprova.
///
/// Reusa o `CsvBrasileiro` pelo mesmo motivo que os relatórios: BOM, `;` e vírgula decimal são o
/// que faz o Excel brasileiro abrir sem "PreÃ§o" e sem tudo na primeira coluna.
/// ===============================================================================</summary>
public static class ModeloImportacaoContatos
{
    public const string NomeDoArquivo = "modelo-importar-contatos.csv";

    /// <summary>⚠️ A PRIMEIRA LINHA É O CABEÇALHO, e cada nome é um dos que `ServicoImportacao`
    /// reconhece. Ele aceita sinônimos ("celular", "cliente", "obs"), mas o modelo oferece UM nome
    /// por coluna: quem recebe a planilha pronta não precisa escolher, e a escolha só existiria
    /// para ser feita errado.
    ///
    /// `nome` e `telefone` são os obrigatórios; os outros três podem ficar em branco — e a segunda
    /// linha de exemplo existe justamente para mostrar isso, em vez de dizer num rodapé.
    ///
    /// ⚠️ OS EXEMPLOS FICAM, e não é descuido. Sem eles o dono inventa o formato do telefone e
    /// descobre o erro depois de preencher a planilha inteira. Deixá-los é seguro porque a
    /// importação tem PRÉVIA: ele vê "Maria Exemplo" na lista antes de confirmar.</summary>
    public static readonly string[][] Linhas =
    [
        ["nome", "telefone", "email", "origem", "observacoes"],
        ["Maria Exemplo", "(84) 98888-7777", "maria@exemplo.com", "indicacao",
         "Apague as duas linhas de exemplo antes de importar"],
        ["João Exemplo", "84977776666", "", "", "Só nome e telefone são obrigatórios"]
    ];

    public static byte[] Gerar() => CsvBrasileiro.Gerar(Linhas);
}
