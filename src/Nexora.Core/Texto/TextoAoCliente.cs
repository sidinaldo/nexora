namespace Nexora.Core.Texto;

/// <summary>===================== AS VARIAVEIS DO TEXTO QUE O CLIENTE RECEBE =====================
/// A pergunta da pesquisa, os agradecimentos dela e o follow-up (BUG-XX, T6) usam as mesmas tres:
/// `{{saudacao}}`, `{{nome}}` e `{{empresa}}`. Um lugar so — eram duas copias, e o follow-up editavel
/// seria a terceira.
///
/// ⚠️ `{{saudacao}}` EXISTE PORQUE `{{nome}}` TEM UMA ARMADILHA, e este projeto ja caiu nela.
/// Quando o WhatsApp nao manda `pushName`, o nome do contato e o telefone formatado, e `Primeiro`
/// devolve NULO — "(84)" nao e nome. Com `"Oi, {{nome}}!"`, sai "Oi, ! Aqui é da...".
/// `Saudacao` decide a PONTUACAO junto com o nome ("Oi, Maria!" ou "Oi!"), e por isso os textos
/// PADRAO usam ela. `{{nome}}` fica disponivel para quem escrever o proprio texto.
/// ========================================================================================</summary>
public static class TextoAoCliente
{
    public static string Preencher(string texto, string nomeDoContato, string nomeDaEmpresa) =>
        texto
            .Replace("{{saudacao}}", NomeDePessoa.Saudacao("Oi", nomeDoContato))
            .Replace("{{nome}}", NomeDePessoa.Primeiro(nomeDoContato) ?? "")
            .Replace("{{empresa}}", nomeDaEmpresa);
}
