namespace Nexora.Core.Entidades;

/// <summary>Uma importação de arquivo, do upload ao resultado (INT-XX).
///
/// ===================== POR QUE ELA É PERSISTIDA =====================
/// A importação da issue #8 não guardava nada: o arquivo subia duas vezes — uma para a prévia,
/// outra para gravar — e eu recusei guardar estado no servidor por não valer a complexidade para
/// economizar um upload.
///
/// O mapeamento mudou essa conta. Entre ver as colunas e decidir o que cada uma vira, existe uma
/// CONVERSA com o usuário, e conversa precisa de memória. E, uma vez que a memória existe, ela
/// paga dois outros problemas de uma vez: o processamento em segundo plano (que precisa de algo
/// para ler depois que a requisição acabou) e o reprocessamento sem subir o arquivo de novo.
/// ====================================================================</summary>
public class Importacao : IEntidadeCriada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>Quem subiu. Importação é escrita em massa que aparece no quadro de todo mundo —
    /// saber de quem foi é o mínimo quando alguém perguntar "de onde vieram estes 600?".</summary>
    public long UsuarioId { get; set; }

    public string NomeArquivo { get; set; } = null!;

    public int TotalLinhas { get; set; }
    public int Importados { get; set; }
    public int Duplicados { get; set; }
    public int Invalidos { get; set; }

    public StatusImportacao Status { get; set; } = StatusImportacao.AguardandoMapeamento;

    /// <summary>Coluna do CSV → campo do Nexora, como `{"phone_number":"telefone"}`.
    ///
    /// `jsonb` porque as chaves são do ARQUIVO DO CLIENTE: cada tenant nomeia as perguntas do
    /// formulário como quer, e não há coluna possível para um conjunto que muda a cada upload.</summary>
    public string? Mapeamento { get; set; }

    /// <summary>⚠️ O ERRO QUE DERRUBOU A IMPORTAÇÃO, quando `Status` é `Erro`. Sem ele a tela
    /// mostraria "deu erro" e ponto, e o dono não teria como saber se o problema é dele (arquivo)
    /// ou nosso (defeito) — a diferença entre corrigir a planilha e abrir um chamado.</summary>
    public string? Erro { get; set; }

    public DateTime CriadoEm { get; set; }

    public ICollection<ImportacaoLinha> Linhas { get; set; } = [];

    public Empresa Empresa { get; set; } = null!;
    public Usuario Usuario { get; set; } = null!;
}

/// <summary>Uma linha do arquivo, com o que aconteceu com ela.
///
/// ⚠️ NÃO É LOG DESCARTÁVEL. Ela é o que a tela de resultado lê para dizer "linha 47: telefone
/// inválido", e o que torna o reprocessamento possível sem pedir o arquivo de novo. Guardar só os
/// contadores responderia "15 falharam" sem responder "quais" — que é a única pergunta que o dono
/// realmente tem.</summary>
public class ImportacaoLinha
{
    public long Id { get; set; }
    public long ImportacaoId { get; set; }

    /// <summary>O número COMO ESTÁ NO EXCEL: o cabeçalho é a linha 1, o primeiro contato é a 2.
    /// Quem for corrigir a planilha procura este número lá — "linha 3 do corpo" não existe na
    /// tela de ninguém.</summary>
    public int NumeroLinha { get; set; }

    /// <summary>A linha inteira como veio, em `jsonb` — inclusive as colunas que ninguém mapeou.
    ///
    /// Guardar tudo é o que permite REPROCESSAR com outro mapeamento: o dono percebe que ligou a
    /// coluna errada, corrige o mapeamento e roda de novo, sem subir o arquivo. Guardar só os
    /// campos mapeados jogaria fora exatamente o que ele precisa para consertar.</summary>
    public string DadosBrutos { get; set; } = null!;

    public ResultadoLinha Resultado { get; set; }

    /// <summary>Por que não entrou. Nulo quando entrou.</summary>
    public string? Motivo { get; set; }

    /// <summary>O contato criado — ou, quando `Duplicado`, o que já existia e foi enriquecido.
    ///
    /// ⚠️ PREENCHIDO NOS DOIS CASOS, e não só no sucesso: é o que deixa a tela de resultado levar
    /// para a pessoa certa quando o dono clica numa linha duplicada perguntando "quem é este?".</summary>
    public long? ContatoId { get; set; }

    public Importacao Importacao { get; set; } = null!;
}

public enum StatusImportacao
{
    AguardandoMapeamento,
    Processando,
    Concluida,
    Erro
}

public enum ResultadoLinha
{
    Importado,
    Duplicado,
    Invalido
}
